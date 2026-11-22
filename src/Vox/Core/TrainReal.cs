using System.Text;
using System.Text.Json;

namespace Vox.Core;

/// <summary>
/// Vox.exe --real-test outDir audioDir history.jsonl model1.bin [model2.bin ...]
/// The user's own saved dictations (Audio\yyyyMMdd-HHmmss.wav, kept when "Keep audio" is on) re-run through each model
/// the way a dictation is (levelled, speech gate on), next to what Vox typed at the time. There is no reference text,
/// so where the models disagree is where to look. Also checks the speech gate never drops real speech.
/// If refs.tsv (file name, what was really said) exists in outDir, each model is scored against it.
/// </summary>
internal static partial class Trainer
{
    public static async Task RealTestAsync(string[] args)
    {
        string outDir = args[1], audioDir = args[2], historyPath = args[3];
        var models = args.Skip(4).Where(File.Exists).ToList();
        Directory.CreateDirectory(outDir);
        var settings = AppSettings.Load();
        var (dict, snippets) = LoadUserData(settings);
        var dictPath = Path.Combine(outDir, "dictionary.json");
        if (File.Exists(dictPath)) dict = JsonSerializer.Deserialize<List<DictEntry>>(File.ReadAllText(dictPath)) ?? dict;

        var history = File.ReadLines(historyPath).Where(l => l.Trim().Length > 0)
            .Select(l => { try { return JsonSerializer.Deserialize<HistoryEntry>(l); } catch { return null; } }).Where(h => h != null).ToList();
        var refs = new Dictionary<string, string>();
        var refPath = Path.Combine(outDir, "refs.tsv");
        if (File.Exists(refPath))
            foreach (var l in File.ReadLines(refPath).Skip(1)) { var p = l.Split('\t'); if (p.Length >= 2 && p[1].Trim().Length > 0) refs[p[0]] = p[1].Trim(); }

        var wavs = Directory.GetFiles(audioDir, "*.wav").OrderBy(f => f).ToList();
        var rows = wavs.Select(w =>
        {
            var name = Path.GetFileNameWithoutExtension(w);
            DateTime.TryParseExact(name, "yyyyMMdd-HHmmss", null, System.Globalization.DateTimeStyles.None, out var t);
            var h = history.OrderBy(x => Math.Abs((x!.Time - t).TotalSeconds)).FirstOrDefault(x => Math.Abs((x!.Time - t).TotalSeconds) < 120);
            return (wav: w, name, typed: h?.Text ?? "", outs: new List<(string model, string gated, string ungated, double ms)>());
        }).ToList();

        foreach (var m in models)
        {
            using var engine = new SpeechEngine();
            await engine.LoadAsync(m);
            // accurate-mode hints as the app would send them; VOX_HINTS=0 turns them off for a comparison run
            engine.Hints = settings.AccurateHints && Environment.GetEnvironmentVariable("VOX_HINTS") != "0" ? TextCleaner.HintTerms(dict) : Array.Empty<string>();
            File.WriteAllLines(Path.Combine(outDir, "hints.txt"), engine.Hints);
            await engine.TranscribeAsync(new float[16000], settings.Language, settings.Threads, "");
            foreach (var r in rows)
            {
                var audio = TrainAudio.Load(r.wav);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var gated = await SpeechGate.TranscribeAsync(engine, audio, settings.Language, settings.Threads, "", condition: true);
                double ms = sw.Elapsed.TotalMilliseconds;
                Environment.SetEnvironmentVariable("VOX_GATE", "0");
                var plain = await SpeechGate.TranscribeAsync(engine, audio, settings.Language, settings.Threads, "", condition: true);
                Environment.SetEnvironmentVariable("VOX_GATE", null);
                r.outs.Add((Path.GetFileNameWithoutExtension(m).Replace("ggml-", ""), TextCleaner.Clean(gated, settings, dict, snippets), TextCleaner.Clean(plain, settings, dict, snippets), ms));
            }
        }

        var md = new StringBuilder($"# Real dictations, re-run ({rows.Count} recordings)\n\n");
        if (refs.Count > 0)
        {
            md.Append($"## Scored against what was really said ({refs.Count} recordings)\n\n| Model | Words right | Perfect |\n|---|---:|---:|\n");
            int w0 = 0, e0 = 0, p0 = 0, n0 = 0;
            foreach (var r in rows.Where(r => refs.ContainsKey(r.name))) { var (w, e, _) = Score(refs[r.name], r.typed); w0 += w; e0 += e; n0++; if (e == 0) p0++; }
            md.Append($"| what Vox typed at the time | {100.0 * (w0 - e0) / Math.Max(1, w0):0.0}% | {p0}/{n0} |\n");
            foreach (var mi in Enumerable.Range(0, models.Count))
            {
                int W = 0, E = 0, P = 0, N = 0;
                foreach (var r in rows.Where(r => refs.ContainsKey(r.name))) { var (w, e, _) = Score(refs[r.name], r.outs[mi].gated); W += w; E += e; N++; if (e == 0) P++; }
                md.Append($"| {rows[0].outs[mi].model} (now) | {100.0 * (W - E) / Math.Max(1, W):0.0}% | {P}/{N} |\n");
            }
            md.Append('\n');
        }
        int dropped = rows.Sum(r => r.outs.Count(o => o.gated.Length == 0 && o.ungated.Length > 0));
        md.Append($"Speech gate: {dropped} of {rows.Count * models.Count} real dictations lost (should be 0).\n\n");
        foreach (var r in rows)
        {
            var a0 = TrainAudio.Load(r.wav); double sec = a0.Length / 16000.0;
            var sp = SpeechGate.Analyze(a0);
            md.Append($"### {r.name} ({sec:0.0} s; speech detector: {sp.Segments} parts, {sp.SpeechSec:0.0} s of speech from {sp.FirstSec:0.0} to {sp.LastSec:0.0} s)\n\n");
            if (refs.TryGetValue(r.name, out var said)) md.Append($"- **said:** {Md(said)}\n");
            md.Append($"- **typed then:** {Md(r.typed)}\n");
            foreach (var o in r.outs)
                md.Append($"- **{o.model}:** {Md(o.gated)}{(o.gated != o.ungated ? $"  _(gate off: {Md(o.ungated)})_" : "")}  ({o.ms / 1000:0.0} s)\n");
            md.Append('\n');
        }
        File.WriteAllText(Path.Combine(outDir, "real-test.md"), md.ToString(), new UTF8Encoding(false));
        if (!File.Exists(refPath))
            File.WriteAllText(refPath, "file\twhat was really said (fill in)\n" + string.Concat(rows.Select(r => $"{r.name}\t\n")), new UTF8Encoding(false));
    }
}
