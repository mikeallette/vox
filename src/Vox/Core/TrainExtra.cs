using System.Text;
using System.Text.RegularExpressions;

namespace Vox.Core;

/// <summary>
/// Extra hands-off checks for the overnight loop:
///   Vox.exe --noise-test outDir model.bin seed       noise with nobody talking: Vox should type nothing
///   Vox.exe --dict-check outDir manifest.tsv ...     the installed dictionary must not change correct sentences
/// </summary>
internal static partial class Trainer
{
    static readonly (string kind, float peak)[] NoiseOnly =
    {
        ("silence", 0.0004f), ("room", 0.01f), ("fan", 0.08f), ("hum", 0.05f),
        ("keyboard", 0.25f), ("kitchen", 0.25f), ("car", 0.15f), ("white", 0.03f),
    };

    public static async Task NoiseTestAsync(string[] args)
    {
        string outDir = args[1], model = args[2];
        int seed = args.Length > 3 && int.TryParse(args[3], out var s0) ? s0 : 1;
        Directory.CreateDirectory(outDir);
        var settings = AppSettings.Load();
        var (dict, snippets) = LoadUserData(settings);
        using var engine = new SpeechEngine();
        await engine.LoadAsync(model);
        string prompt = TextCleaner.BuildPrompt(dict);
        var rng = new Random(seed);
        var tsv = new StringBuilder("kind\tmode\tseconds\twords_typed\ttext\n");
        foreach (var (kind, peak) in NoiseOnly)
        {
            int len = TrainAudio.Rate * (4 + rng.Next(6));
            var audio = TrainAudio.AtPeak(TrainAudio.NoiseBed(kind, len, seed * 31 + kind.Length, _ => Array.Empty<float>()), peak);
            foreach (var mode in new[] { "whole", "live" })
            {
                var r = await Transcribe(engine, settings, prompt, audio, mode);
                var clean = TextCleaner.Clean(r.Raw ?? "", settings, dict, snippets);
                int words = clean.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
                tsv.Append($"{kind}\t{mode}\t{len / TrainAudio.Rate}\t{words}\t{clean.Replace("\t", " ").Replace("\n", " / ")}\n");
            }
        }
        File.WriteAllText(Path.Combine(outDir, "noise-test.tsv"), tsv.ToString(), new UTF8Encoding(false));
    }

    /// <summary>
    /// A/B test of the speech gate: Vox.exe --gate-test outDir model.bin manifest.tsv
    /// The same noise-only clips and quiet or short speech clips, run the way a real dictation is (levelled audio,
    /// whole clip), first with the gate off, then on. The gate must stop noise words without losing speech.
    /// </summary>
    public static async Task GateTestAsync(string[] args)
    {
        string outDir = args[1], model = args[2], manifest = args[3];
        Directory.CreateDirectory(outDir);
        var progress = Path.Combine(outDir, "progress-gate.txt");
        try
        {
            var settings = AppSettings.Load();
            var (dict, snippets) = LoadUserData(settings);
            using var engine = new SpeechEngine();
            await engine.LoadAsync(model);
            if (!SpeechGate.Available) throw new InvalidOperationException("speech gate model not found next to " + model);
            await engine.TranscribeAsync(new float[16000], settings.Language, settings.Threads, "");

            // test material
            var items = new List<(string set, string kind, string written, float[] audio)>();
            // the noise test from the overnight loop (same seeds, so the r002 car clip that gave "Actualmente." is included), plus more
            for (int seed = 1; seed <= 24; seed++)
            {
                int s0 = seed <= 12 ? 1000 + seed : seed;
                var rng = new Random(s0);
                foreach (var (kind, peak) in NoiseOnly)
                {
                    int len = TrainAudio.Rate * (4 + rng.Next(6));
                    items.Add((seed <= 12 ? "noise-raw" : "noise", kind, "", TrainAudio.AtPeak(TrainAudio.NoiseBed(kind, len, s0 * 31 + kind.Length, _ => Array.Empty<float>()), peak)));
                }
            }
            var clips = ReadManifest(manifest).Where(c => c.Kind == "sentence").ToList();
            var pool = BackgroundPool(clips);
            var shortest = clips.OrderBy(c => c.Written.Split(' ').Length).ThenBy(c => c.Id).Where((_, i) => i % 2 == 0).Take(40).ToList();
            var conds = new[] { "studio", "quiet-mic", "very-quiet", "far-room", "car", "kitchen" };
            for (int i = 0; i < shortest.Count; i++)
                foreach (var cond in new[] { conds[i % conds.Length], conds[(i + 3) % conds.Length] })
                    items.Add(("speech", cond, shortest[i].Written, TrainAudio.Make(cond, TrainAudio.Load(shortest[i].Wav), Hash(shortest[i].Wav + cond), pool)));

            var rows = new List<(string set, string kind, string written, string off, string on, long vadMs)>();
            int n = 0;
            foreach (var it in items)
            {
                n++;
                Environment.SetEnvironmentVariable("VOX_GATE", "0");
                bool cond = it.set != "noise-raw";      // raw = exactly the overnight loop noise test; the rest levelled like a dictation
                var off = await SpeechGate.TranscribeAsync(engine, it.audio, settings.Language, settings.Threads, "", condition: cond);
                Environment.SetEnvironmentVariable("VOX_GATE", null);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                SpeechGate.Analyze(it.audio);
                long vadMs = sw.ElapsedMilliseconds;
                var on = await SpeechGate.TranscribeAsync(engine, it.audio, settings.Language, settings.Threads, "", condition: cond);
                rows.Add((it.set, it.kind, it.written, TextCleaner.Clean(off, settings, dict, snippets), TextCleaner.Clean(on, settings, dict, snippets), vadMs));
                if (n % 10 == 0) File.WriteAllText(progress, $"{n}/{items.Count}\n");
            }

            // ---- report ----
            static int Words(string s) => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            var md = new StringBuilder("# Speech gate A/B test\n\n");
            var noise = rows.Where(r => r.set.StartsWith("noise")).ToList();
            var speech = rows.Where(r => r.set == "speech").ToList();
            int nOffBad = noise.Count(r => r.off.Length > 0), nOnBad = noise.Count(r => r.on.Length > 0);
            md.Append($"## Noise with nobody talking ({noise.Count} clips, half levelled like a dictation, half raw)\n\n");
            md.Append($"| | Gate off | Gate on |\n|---|---:|---:|\n| Clips where Vox typed something | {nOffBad} | {nOnBad} |\n| Words typed | {noise.Sum(r => Words(r.off))} | {noise.Sum(r => Words(r.on))} |\n\n");
            foreach (var r in noise.Where(r => r.off.Length > 0 || r.on.Length > 0)) md.Append($"- {r.kind} ({r.set}): off \"{Md(r.off)}\", on \"{Md(r.on)}\"\n");
            int refW = 0, eOff = 0, eOn = 0, lost = 0;
            foreach (var r in speech) { var a = Score(r.written, r.off); var b = Score(r.written, r.on); refW += a.refWords; eOff += a.err; eOn += b.err; if (r.off.Length > 0 && r.on.Length == 0) lost++; }
            md.Append($"\n## Short and quiet speech ({speech.Count} clips)\n\n");
            md.Append($"| | Gate off | Gate on |\n|---|---:|---:|\n| Words right | {100.0 * (refW - eOff) / Math.Max(1, refW):0.0}% | {100.0 * (refW - eOn) / Math.Max(1, refW):0.0}% |\n| Dictations lost completely | | {lost} |\n\n");
            md.Append("By condition:\n\n| Condition | Clips | Off | On |\n|---|---:|---:|---:|\n");
            foreach (var g in speech.GroupBy(r => r.kind))
            {
                int w = 0, a = 0, b = 0; foreach (var r in g) { var x = Score(r.written, r.off); var y = Score(r.written, r.on); w += x.refWords; a += x.err; b += y.err; }
                md.Append($"| {g.Key} | {g.Count()} | {100.0 * (w - a) / Math.Max(1, w):0.0}% | {100.0 * (w - b) / Math.Max(1, w):0.0}% |\n");
            }
            var changed = speech.Where(r => r.off != r.on).ToList();
            md.Append($"\n### Speech results the gate changed ({changed.Count})\n\n| Should be | Gate off | Gate on |\n|---|---|---|\n");
            foreach (var r in changed) md.Append($"| {Md(r.written)} | {Md(r.off)} | {Md(r.on)} |\n");
            var ms = rows.Select(r => r.vadMs).OrderBy(x => x).ToList();
            md.Append($"\nSpeech detector time per clip: median {ms[ms.Count / 2]} ms, worst {ms[^1]} ms.\n");
            File.WriteAllText(Path.Combine(outDir, "gate-test.md"), md.ToString(), new UTF8Encoding(false));
            File.WriteAllText(progress, "DONE\n");
        }
        catch (Exception ex) { File.AppendAllText(progress, "FAILED: " + ex + "\n"); }
    }

    public static void DictCheck(string[] args)
    {
        string outDir = args[1];
        Directory.CreateDirectory(outDir);
        var settings = AppSettings.Load();
        var (dict, _) = LoadUserData(settings);
        var refs = args.Skip(2).Where(File.Exists).SelectMany(ReadManifest).Select(c => c.Written).Distinct().ToList();
        var sb = new StringBuilder("rule\tcorrect sentence\tbecomes\n");
        int hits = 0;
        foreach (var d in dict)
        {
            if (string.IsNullOrWhiteSpace(d.To)) continue;
            var heard = string.IsNullOrWhiteSpace(d.From) ? d.To : d.From;
            var rx = new Regex($@"(?<![\w]){Regex.Escape(heard.Trim())}(?![\w])", RegexOptions.IgnoreCase);
            foreach (var w in refs)
            {
                var after = rx.Replace(w, m => TextCleaner.KeepSentenceCap(w, m, d.To));
                if (after == w) continue;
                hits++;
                sb.Append($"{heard} -> {d.To}\t{w.Replace("\n", " / ")}\t{after.Replace("\n", " / ")}\n");
            }
        }
        File.WriteAllText(Path.Combine(outDir, "dict-check.tsv"), sb.ToString(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(outDir, "dict-check.txt"), $"{dict.Count} dictionary rules checked against {refs.Count} correct sentences: {hits} would change a correct sentence\n");
    }
}
