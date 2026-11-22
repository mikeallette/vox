using SherpaOnnx;

namespace Vox.Core;

public readonly record struct SpeakerTurn(double Start, double End, int Speaker);

/// <summary>
/// Offline speaker diarization ("who spoke when") with sherpa-onnx: a pyannote segmentation model finds speech turns,
/// a speaker-embedding model turns each turn into a voice fingerprint, and the fingerprints are clustered into people.
/// Everything runs on the CPU in this process. The two model files live in &lt;data&gt;\models\speakers (or
/// &lt;app&gt;\models\speakers); without them meetings simply keep the "Others" label.
/// </summary>
public sealed class SpeakerDiarizer : IDisposable
{
    public const string FolderName = "speakers";

    /// <summary>Cosine-distance threshold for "same person" (lower = more speakers). Tuned on the synthetic meeting test; VOX_SPK_THRESHOLD overrides.</summary>
    public static float Threshold =>
        float.TryParse(Environment.GetEnvironmentVariable("VOX_SPK_THRESHOLD"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : DefaultThreshold;
    const float DefaultThreshold = 0.6f;

    readonly OfflineSpeakerDiarization _sd;

    SpeakerDiarizer(string segModel, string embModel, int threads)
    {
        int n = threads > 0 ? threads : Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
        var cfg = new OfflineSpeakerDiarizationConfig();
        cfg.Segmentation.Pyannote.Model = segModel;
        cfg.Segmentation.NumThreads = n;
        cfg.Segmentation.Provider = "cpu";
        cfg.Embedding.Model = embModel;
        cfg.Embedding.NumThreads = n;
        cfg.Embedding.Provider = "cpu";
        cfg.Clustering.NumClusters = -1;          // unknown number of people
        cfg.Clustering.Threshold = Threshold;
        cfg.MinDurationOn = 0.3f;                 // ignore blips shorter than this
        cfg.MinDurationOff = 0.5f;                // join turns of one person split by a short pause
        _sd = new OfflineSpeakerDiarization(cfg);
        var ec = new SpeakerEmbeddingExtractorConfig();
        ec.Model = embModel; ec.NumThreads = n; ec.Provider = "cpu";
        _emb = new SpeakerEmbeddingExtractor(ec);
    }

    readonly SpeakerEmbeddingExtractor _emb;

    /// <summary>Voice fingerprint of one stretch of 16 kHz audio (unit length, so a dot product is the cosine similarity).</summary>
    public float[] Embed(float[] samples16k)
    {
        using var st = _emb.CreateStream();
        st.AcceptWaveform(16000, samples16k); st.InputFinished();
        var v = _emb.Compute(st);
        double n = Math.Sqrt(v.Sum(x => (double)x * x));
        if (n > 0) for (int i = 0; i < v.Length; i++) v[i] = (float)(v[i] / n);
        return v;
    }

    public static double Similarity(float[] a, float[] b) { double d = 0; for (int i = 0; i < a.Length; i++) d += a[i] * b[i]; return d; }

    /// <summary>
    /// Second pass on whole transcript lines (each line is cut at a pause, so it is usually one person):
    /// fingerprints every line of 1.2 s or more, merges labels whose average voices are nearly the same person,
    /// then moves a line to another label when its voice is clearly closer to that label's average.
    /// Fixes short turns the first pass folded into a neighbor, and one person split into two labels.
    /// </summary>
    public static int[] Refine(IReadOnlyList<float[]?> lineEmb, int[] labels, double mergeAbove = 0.70, double moveMargin = 0.10)
    {
        var lab = (int[])labels.Clone();
        Dictionary<int, float[]> Centroids()
        {
            var c = new Dictionary<int, float[]>();
            foreach (var g in Enumerable.Range(0, lab.Length).Where(i => lab[i] >= 0 && lineEmb[i] != null).GroupBy(i => lab[i]))
            {
                var sum = new float[lineEmb[g.First()]!.Length];
                foreach (var i in g) for (int k = 0; k < sum.Length; k++) sum[k] += lineEmb[i]![k];
                double n = Math.Sqrt(sum.Sum(x => (double)x * x));
                if (n > 0) for (int k = 0; k < sum.Length; k++) sum[k] = (float)(sum[k] / n);
                c[g.Key] = sum;
            }
            return c;
        }
        // merge near-identical labels (most similar pair first)
        while (true)
        {
            var c = Centroids(); var keys = c.Keys.ToList();
            (int a, int b, double s) best = (-1, -1, 0);
            for (int i = 0; i < keys.Count; i++) for (int j = i + 1; j < keys.Count; j++)
            {
                double s = Similarity(c[keys[i]], c[keys[j]]);
                if (s > best.s) best = (keys[i], keys[j], s);
            }
            if (best.a < 0 || best.s < mergeAbove) break;
            for (int i = 0; i < lab.Length; i++) if (lab[i] == best.b) lab[i] = best.a;
        }
        // move lines that clearly sound like another label (compare against centroids without the line itself)
        var fixedLab = (int[])lab.Clone();
        var sums = new Dictionary<int, float[]>();
        for (int i = 0; i < lab.Length; i++)
        {
            if (lab[i] < 0 || lineEmb[i] == null) continue;
            if (!sums.TryGetValue(lab[i], out var s)) sums[lab[i]] = s = new float[lineEmb[i]!.Length];
            for (int k = 0; k < s.Length; k++) s[k] += lineEmb[i]![k];
        }
        static double CosTo(float[] v, float[] sum, float[]? minus)
        {
            double d = 0, n = 0;
            for (int k = 0; k < v.Length; k++) { double c = sum[k] - (minus?[k] ?? 0); d += v[k] * c; n += c * c; }
            return n > 1e-9 ? d / Math.Sqrt(n) : double.NaN;
        }
        for (int i = 0; i < lab.Length; i++)
        {
            if (lineEmb[i] == null || lab[i] < 0 || sums.Count < 2) continue;
            double own = CosTo(lineEmb[i]!, sums[lab[i]], lineEmb[i]);      // the label's voice without this line
            if (double.IsNaN(own)) own = mergeAbove - moveMargin;           // the label is only this line: move it only if it matches another voice as well as a merge would
            var (bestLab, bestSim) = sums.Where(kv => kv.Key != lab[i]).Select(kv => (kv.Key, CosTo(lineEmb[i]!, kv.Value, null))).OrderByDescending(x => x.Item2).First();
            if (bestSim - own > moveMargin) fixedLab[i] = bestLab;
        }
        return fixedLab;
    }

    /// <summary>Folders searched for the speaker models (data folder first).</summary>
    public static IEnumerable<string> ModelDirs(string dataModelsDir) => new[]
    {
        Path.Combine(dataModelsDir, FolderName),
        Path.Combine(AppContext.BaseDirectory, "models", FolderName),
    };

    /// <summary>(segmentation, embedding) model paths, or nulls when not installed.</summary>
    /// <remarks>
    /// Each model is looked up on its own: the small segmentation model ships inside the app (&lt;app&gt;\models\speakers),
    /// the larger voice-fingerprint model is downloaded by the user and imported into the data folder.
    /// </remarks>
    public static (string? Seg, string? Emb) FindModels(string dataModelsDir)
    {
        var onnx = ModelDirs(dataModelsDir).Where(Directory.Exists).SelectMany(d => Directory.GetFiles(d, "*.onnx")).ToList();
        return (onnx.FirstOrDefault(IsSegmentationModel), onnx.FirstOrDefault(f => !IsSegmentationModel(f)));
    }

    public static bool IsSegmentationModel(string path)
    {
        var n = Path.GetFileName(path).ToLowerInvariant();
        return n.Contains("segmentation") || n.Contains("pyannote");
    }

    public static bool IsInstalled(string dataModelsDir) => FindModels(dataModelsDir).Seg != null;

    public static SpeakerDiarizer? TryCreate(string dataModelsDir, int threads = 0)
    {
        var (seg, emb) = FindModels(dataModelsDir);
        return seg == null || emb == null ? null : new SpeakerDiarizer(seg, emb, threads);
    }

    /// <summary>Copies chosen model files into &lt;data&gt;\models\speakers. Returns a message for the user.</summary>
    public static string Import(string dataModelsDir, IEnumerable<string> files)
    {
        var dir = Path.Combine(dataModelsDir, FolderName);
        Directory.CreateDirectory(dir);
        int seg = 0, emb = 0;
        foreach (var f in files.Where(f => f.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)))
        {
            // the pyannote download names its file "model.onnx"; give it a name Vox recognizes
            bool isSeg = IsSegmentationModel(f) || Path.GetFileName(f).StartsWith("model", StringComparison.OrdinalIgnoreCase);
            if (isSeg)
            {
                foreach (var old in Directory.GetFiles(dir, "*.onnx").Where(IsSegmentationModel)) File.Delete(old);
                File.Copy(f, Path.Combine(dir, "pyannote-segmentation.onnx"), true); seg++;
            }
            else
            {
                foreach (var old in Directory.GetFiles(dir, "*.onnx").Where(o => !IsSegmentationModel(o))) File.Delete(old);
                File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), true); emb++;
            }
        }
        var (s, e) = FindModels(dataModelsDir);
        return s != null && e != null ? "Speaker models installed. New meetings will label other voices Speaker 1, Speaker 2, ..."
             : s == null ? "Still missing the segmentation model (the pyannote model.onnx)."
             : "Still missing the voice-fingerprint model (nemo_en_titanet_small.onnx).";
    }

    /// <summary>Who spoke when in 16 kHz mono audio. Speaker numbers are per call.</summary>
    public List<SpeakerTurn> Run(float[] samples16k)
    {
        if (samples16k.Length < 16000) return new();
        var segs = _sd.Process(samples16k);
        return segs.Select(s => new SpeakerTurn(s.Start, s.End, s.Speaker)).OrderBy(t => t.Start).ToList();
    }

    /// <summary>
    /// The speaker runs inside [start, end]: turns clipped to the window, consecutive turns of one person joined,
    /// and runs under 1 s folded into a neighbor (too short to judge or to transcribe on their own).
    /// </summary>
    public static List<SpeakerTurn> RunsWithin(List<SpeakerTurn> turns, double start, double end)
    {
        var runs = new List<SpeakerTurn>();
        foreach (var t in turns)
        {
            double a = Math.Max(t.Start, start), b = Math.Min(t.End, end);
            if (b - a <= 0.05) continue;
            if (runs.Count > 0 && runs[^1].Speaker == t.Speaker) runs[^1] = runs[^1] with { End = b };
            else runs.Add(new SpeakerTurn(a, b, t.Speaker));
        }
        // fold short runs into the longer neighbor, then re-join
        bool changed = true;
        while (changed && runs.Count > 1)
        {
            changed = false;
            int i = Enumerable.Range(0, runs.Count).Where(k => runs[k].End - runs[k].Start < 1.0).OrderBy(k => runs[k].End - runs[k].Start).DefaultIfEmpty(-1).First();
            if (i < 0) break;
            int j = i == 0 ? 1 : i == runs.Count - 1 ? i - 1 : (runs[i - 1].End - runs[i - 1].Start >= runs[i + 1].End - runs[i + 1].Start ? i - 1 : i + 1);
            var keep = runs[j];
            runs[j] = keep with { Start = Math.Min(keep.Start, runs[i].Start), End = Math.Max(keep.End, runs[i].End) };
            runs.RemoveAt(i);
            for (int k = runs.Count - 1; k > 0; k--)
                if (runs[k].Speaker == runs[k - 1].Speaker) { runs[k - 1] = runs[k - 1] with { End = runs[k].End }; runs.RemoveAt(k); }
            changed = true;
        }
        // stretch the first and last run to the window edges so no words fall outside
        if (runs.Count > 0) { runs[0] = runs[0] with { Start = start }; runs[^1] = runs[^1] with { End = end }; }
        // and close the gaps between runs at their midpoints
        for (int k = 1; k < runs.Count; k++)
        {
            double mid = (runs[k - 1].End + runs[k].Start) / 2;
            runs[k - 1] = runs[k - 1] with { End = mid }; runs[k] = runs[k] with { Start = mid };
        }
        return runs;
    }

    /// <summary>Speaker of the turn closest to a time (for a line the diarizer found no speech in).</summary>
    public static int Nearest(List<SpeakerTurn> turns, double t) =>
        turns.OrderBy(x => t < x.Start ? x.Start - t : t > x.End ? t - x.End : 0).First().Speaker;

    public void Dispose() { _sd.Dispose(); _emb.Dispose(); }
}
