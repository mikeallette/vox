using System.Diagnostics;
using System.Globalization;
using System.Text;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Vox.Core;

/// <summary>
/// Vox.exe --meeting-test workDir speechModel.bin speakerModelsDir
/// Builds a meeting from tests\meeting\Make-MeetingTest.ps1 output (clips + script.tsv), runs it through the real
/// meeting pipeline (phrase cutting, transcription, echo removal, speaker labels) and grades it against the script.
/// Options (environment): VOX_MT_SEED (timeline), VOX_MT_BLEED=0.3 (the mic also hears the call, like laptop speakers),
/// VOX_MT_QUICK=1 (all turn changes have short gaps, so many lines hold two people), VOX_SPK_THRESHOLD.
/// </summary>
internal static class MeetingTest
{
    sealed record Turn(double Start, double End, string Person, string Track, string Text);

    public static async Task RunAsync(string[] args)
    {
        string work = Path.GetFullPath(args[1]), model = args[2], spkDir = args[3];
        var report = new StringBuilder();
        try
        {
            int seed = int.TryParse(Environment.GetEnvironmentVariable("VOX_MT_SEED"), out var sd) ? sd : 1;
            float bleed = float.TryParse(Environment.GetEnvironmentVariable("VOX_MT_BLEED"), NumberStyles.Float, CultureInfo.InvariantCulture, out var bl) ? bl : 0f;
            bool quick = Environment.GetEnvironmentVariable("VOX_MT_QUICK") == "1";
            var rng = new Random(seed);

            // ----- timeline -----
            var lines = File.ReadAllLines(Path.Combine(work, "script.tsv")).Skip(1).Select(l => l.Split('\t')).ToList();
            var clips = lines.Select(c => Load16k(Path.Combine(work, "clips", c[0]))).ToList();
            double t = 1.0; var turns = new List<Turn>();
            string? prev = null;
            for (int i = 0; i < lines.Count; i++)
            {
                string person = lines[i][2];
                if (prev != null)
                {
                    // same person continuing: a normal pause; turn change: alternate quick hand-offs (one line may hold two
                    // people) with slower ones
                    double gap = person == prev ? 0.5 + rng.NextDouble() * 0.4
                               : quick || rng.Next(2) == 0 ? 0.2 + rng.NextDouble() * 0.2 : 0.9 + rng.NextDouble() * 0.8;
                    t += gap;
                }
                double len = clips[i].Length / 16000.0;
                turns.Add(new Turn(t, t + len, person, lines[i][1], lines[i][3]));
                t += len; prev = person;
            }
            int total = (int)((t + 1.5) * 16000);
            var playback = new float[total]; var mic = new float[total];
            for (int i = 0; i < turns.Count; i++)
            {
                var dst = turns[i].Track == "mic" ? mic : playback;
                int at = (int)(turns[i].Start * 16000);
                for (int k = 0; k < clips[i].Length && at + k < total; k++) dst[at + k] += clips[i][k] * 0.6f;
            }
            // quiet background hiss on both, and (optionally) laptop speakers leaking the call into the mic, 20 ms late
            var nr = new Random(seed + 100);
            for (int k = 0; k < total; k++)
            {
                playback[k] += (float)(nr.NextDouble() - 0.5) * 0.002f;
                mic[k] += (float)(nr.NextDouble() - 0.5) * 0.004f + (bleed > 0 && k >= 320 ? playback[k - 320] * bleed : 0f);
            }

            // ----- run the real pipeline in a scratch data folder -----
            var dataDir = Path.Combine(work, "data");
            if (Directory.Exists(dataDir)) Directory.Delete(dataDir, true);
            var data = new DataStore(dataDir);
            if (Directory.Exists(spkDir))
            {
                var to = Path.Combine(data.ModelsDir, SpeakerDiarizer.FolderName);
                Directory.CreateDirectory(to);
                foreach (var f in Directory.GetFiles(spkDir, "*.onnx")) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
            }
            using var engine = new SpeechEngine();
            await engine.LoadAsync(model);
            var settings = new AppSettings { Language = "auto", MeetingSpeakerLabels = true };
            var rec = new MeetingRecorder(settings, data, engine);
            var sw = Stopwatch.StartNew();
            var path = await rec.RunRecordedAsync("Meeting test", mic, new[] { playback });
            double secs = sw.Elapsed.TotalSeconds;
            var outLines = rec.FinalLines;

            // ----- grade -----
            report.AppendLine($"meeting: {t:0} s, {turns.Count} turns, people on the call: {string.Join(", ", turns.Where(x => x.Track == "playback").Select(x => x.Person).Distinct())}");
            report.AppendLine($"seed {seed}, quick hand-offs {(quick ? "all" : "half")}, mic bleed {bleed:0.00}, threshold {SpeakerDiarizer.Threshold:0.00}");
            report.AppendLine($"processing after the meeting ended: {secs:0.0} s (transcription + speaker labels; live meetings transcribe while running)");

            // speaker labels: duration-weighted confusion between labels and true people (playback track only)
            var conf = new Dictionary<string, Dictionary<string, double>>();
            foreach (var o in outLines.Where(o => o.Source > 0))
                foreach (var tr in turns.Where(x => x.Track == "playback"))
                {
                    double ov = Math.Min(o.End, tr.End) - Math.Max(o.Start, tr.Start);
                    if (ov <= 0) continue;
                    if (!conf.TryGetValue(o.Speaker, out var row)) conf[o.Speaker] = row = new();
                    row[tr.Person] = row.GetValueOrDefault(tr.Person) + ov;
                }
            double all = conf.Values.Sum(r => r.Values.Sum()), pure = conf.Values.Sum(r => r.Values.Max());
            report.AppendLine($"labels found: {conf.Count} (true: {turns.Where(x => x.Track == "playback").Select(x => x.Person).Distinct().Count()})   label purity: {(all > 0 ? pure / all : 0):P1}");
            foreach (var (lab, row) in conf.OrderBy(k => k.Key))
                report.AppendLine($"   {lab,-10} " + string.Join("  ", row.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value:0.0}s")));
            var labelPerson = conf.ToDictionary(k => k.Key, k => k.Value.OrderByDescending(v => v.Value).First().Key);

            // each true turn: was it written under the right person (or You)?
            int right = 0;
            foreach (var tr in turns)
            {
                var hit = outLines.Where(o => Math.Min(o.End, tr.End) - Math.Max(o.Start, tr.Start) > 0.3 * (tr.End - tr.Start))
                                  .Select(o => o.Speaker == "You" ? "You" : labelPerson.GetValueOrDefault(o.Speaker, o.Speaker)).ToList();
                if (hit.Count > 0 && hit.All(h => h == tr.Person)) right++;
            }
            report.AppendLine($"turns written under the right person: {right}/{turns.Count}");

            // words
            var truth = string.Join(" ", turns.Select(x => x.Text));
            var got = string.Join(" ", outLines.Select(o => o.Text));
            var (errs, n) = WordErrors(truth, got);
            report.AppendLine($"word accuracy (whole meeting): {1 - (double)errs / n:P1}  ({errs} errors / {n} words)");
            int youLines = outLines.Count(o => o.Source == 0), youTrue = turns.Count(x => x.Track == "mic");
            report.AppendLine($"You lines: {youLines} (true {youTrue}; extra ones would be the call leaking into the mic)");

            // Rename speakers: put the true names on a copy of the saved file and check every line label
            var copy = Path.Combine(work, "renamed.md");
            File.Copy(path!, copy, true);
            var found = MeetingRecorder.SpeakersIn(copy);
            MeetingRecorder.RenameSpeakers(copy, labelPerson.ToDictionary(k => k.Key, k => k.Value + " (renamed)"));
            var after = MeetingRecorder.SpeakersIn(copy);
            report.AppendLine($"rename: labels in file before [{string.Join(", ", found)}] after [{string.Join(", ", after)}]");

            report.AppendLine();
            report.AppendLine("TRANSCRIPT");
            foreach (var o in outLines) report.AppendLine($"[{o.Start,6:0.0}-{o.End,6:0.0}] {o.Speaker}: {o.Text}");
            report.AppendLine();
            report.AppendLine("SCRIPT");
            foreach (var tr in turns) report.AppendLine($"[{tr.Start,6:0.0}-{tr.End,6:0.0}] {tr.Person}: {tr.Text}");
            report.AppendLine();
            report.AppendLine("FILE: " + path);
            report.AppendLine(File.ReadAllText(path!));
            report.AppendLine("OK");
        }
        catch (Exception ex) { report.AppendLine("FAILED: " + ex); }
        File.WriteAllText(Path.Combine(work, "report.txt"), report.ToString());
    }

    /// <summary>Vox.exe --spk-probe workDir embeddingModel.onnx : how alike the voice fingerprints of each script line are.</summary>
    public static void Probe(string[] args)
    {
        var sb = new StringBuilder();
        try
        {
            string work = args[1];
            var cfg = new SherpaOnnx.SpeakerEmbeddingExtractorConfig();
            cfg.Model = args[2]; cfg.NumThreads = 4; cfg.Provider = "cpu";
            using var ex = new SherpaOnnx.SpeakerEmbeddingExtractor(cfg);
            var lines = File.ReadAllLines(Path.Combine(work, "script.tsv")).Skip(1).Select(l => l.Split('\t')).ToList();
            var embs = new List<(string P, float[] E)>();
            foreach (var l in lines)
            {
                var x = Load16k(Path.Combine(work, "clips", l[0]));
                using var st = ex.CreateStream();
                st.AcceptWaveform(16000, x); st.InputFinished();
                embs.Add((l[2], ex.Compute(st)));
            }
            static double Cos(float[] a, float[] b) { double d = 0, na = 0, nb = 0; for (int i = 0; i < a.Length; i++) { d += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; } return d / Math.Sqrt(na * nb); }
            var people = embs.Select(e => e.P).Distinct().ToList();
            sb.AppendLine("average cosine similarity between lines (1 = identical voice):");
            sb.AppendLine("          " + string.Join("", people.Select(p => $"{p,8}")));
            foreach (var p in people)
            {
                sb.Append($"{p,-10}");
                foreach (var q in people)
                {
                    var v = new List<double>();
                    for (int i = 0; i < embs.Count; i++) for (int j = 0; j < embs.Count; j++)
                        if (i != j && embs[i].P == p && embs[j].P == q) v.Add(Cos(embs[i].E, embs[j].E));
                    sb.Append($"{(v.Count > 0 ? v.Average() : double.NaN),8:0.00}");
                }
                sb.AppendLine();
            }
        }
        catch (Exception e) { sb.AppendLine("FAILED: " + e); }
        File.WriteAllText(Path.Combine(args[1], "probe.txt"), sb.ToString());
    }

    /// <summary>
    /// Vox.exe --loopback-test outDir clip.wav : records every playback device the way a meeting does while this process
    /// plays a short clip quietly at 2 s and again at 12 s (silence in between), then checks that each device's recording
    /// kept time, i.e. the sound shows up at the right moments even though Windows sends nothing during the silence.
    /// </summary>
    public static async Task LoopbackAsync(string[] args)
    {
        var sb = new StringBuilder();
        string outDir = args[1];
        Directory.CreateDirectory(outDir);
        var sources = new List<PlaybackSource>();
        try
        {
            var clip = Load16k(args[2]).Select(v => v * 0.25f).ToArray();     // played quietly
            var clock = Stopwatch.StartNew();
            int segs = 0;
            using (var en = new NAudio.CoreAudioApi.MMDeviceEnumerator())
            {
                var def = en.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
                var com = en.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Communications);
                sb.AppendLine($"default playback: {def.FriendlyName}");
                sb.AppendLine($"default communications playback: {com.FriendlyName}");
                foreach (var d in en.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active))
                {
                    int i = sources.Count + 1;
                    sources.Add(new PlaybackSource(i, d, clock, _ => Interlocked.Increment(ref segs), Path.Combine(outDir, $"device{i}.wav")));
                    sb.AppendLine($"recording device {i}: {d.FriendlyName}");
                }
            }
            var timer = new Timer(_ => { foreach (var s in sources) s.Tick(); }, null, 250, 250);
            async Task PlayAt(double sec)
            {
                var wait = TimeSpan.FromSeconds(sec) - clock.Elapsed; if (wait > TimeSpan.Zero) await Task.Delay(wait);
                var started = clock.Elapsed.TotalSeconds;
                using var wo = new WaveOutEvent();
                var prov = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(new RawSamples(clip), 48000);
                wo.Init(prov); wo.Play();
                while (wo.PlaybackState == PlaybackState.Playing) await Task.Delay(50);
                sb.AppendLine($"played clip at {started:0.00} s ({clip.Length / 16000.0:0.0} s long)");
            }
            await PlayAt(2); await PlayAt(12);
            await Task.Delay(TimeSpan.FromSeconds(18) - clock.Elapsed);
            timer.Dispose();
            foreach (var s in sources) s.Finish();
            sb.AppendLine($"stopped at {clock.Elapsed.TotalSeconds:0.00} s; phrases cut: {segs}");
            foreach (var s in sources)
            {
                s.Dispose();
                using var r = new WaveFileReader(s.WavPath!);
                var x = new float[r.SampleCount]; var b = new byte[r.Length]; r.Read(b, 0, b.Length);
                for (int i = 0; i < x.Length; i++) x[i] = BitConverter.ToInt16(b, i * 2) / 32768f;
                // sound onsets: 100 ms windows above a small level after at least 1 s of quiet
                var on = new List<double>(); int quiet = 0;
                for (int w = 0; w + 1600 <= x.Length; w += 1600)
                {
                    double e = 0; for (int k = 0; k < 1600; k++) e += x[w + k] * x[w + k];
                    bool loud = Math.Sqrt(e / 1600) > 0.003;
                    if (loud && quiet >= 10) on.Add((s.OffsetSec * 16000 + w) / 16000.0);
                    quiet = loud ? 0 : quiet + 1;
                }
                sb.AppendLine($"device {s.Index} ({s.Name}): {x.Length / 16000.0:0.00} s recorded, heard sound: {s.HeardSound}, sound starts at: {string.Join(", ", on.Select(v => v.ToString("0.00")))}");
            }
            sb.AppendLine("OK");
        }
        catch (Exception e) { sb.AppendLine("FAILED: " + e); }
        foreach (var s in sources) s.Dispose();
        File.WriteAllText(Path.Combine(outDir, "loopback.txt"), sb.ToString());
    }

    sealed class RawSamples : ISampleProvider
    {
        readonly float[] _x; int _pos;
        public RawSamples(float[] x) { _x = x; }
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(16000, 1);
        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(count, _x.Length - _pos);
            Array.Copy(_x, _pos, buffer, offset, n); _pos += n; return n;
        }
    }

    static float[] Load16k(string path)
    {
        using var r = new AudioFileReader(path);
        ISampleProvider sp = r;
        if (sp.WaveFormat.Channels > 1) sp = new StereoToMonoSampleProvider(sp);
        if (sp.WaveFormat.SampleRate != 16000) sp = new WdlResamplingSampleProvider(sp, 16000);
        var list = new List<float>(); var buf = new float[16000]; int n;
        while ((n = sp.Read(buf, 0, buf.Length)) > 0) list.AddRange(buf.AsSpan(0, n).ToArray());
        return list.ToArray();
    }

    static string[] Norm(string s) => new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c == ' ' ? c : ' ').ToArray())
        .Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Word-level edit distance (numbers written as digits vs words count as errors; the script spells numbers out).</summary>
    static (int Errors, int Words) WordErrors(string truth, string got)
    {
        var a = Norm(truth); var b = Norm(got);
        var d = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) d[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            int prevDiag = d[0]; d[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int tmp = d[j];
                d[j] = Math.Min(Math.Min(d[j] + 1, d[j - 1] + 1), prevDiag + (a[i - 1] == b[j - 1] ? 0 : 1));
                prevDiag = tmp;
            }
        }
        return (d[b.Length], a.Length);
    }
}
