using System.Diagnostics;
using System.Text;

namespace Vox.Core;

/// <summary>
/// Vox.exe --live-replay outFile refs.tsv wavDir model.bin
/// Plays each saved recording (wavDir\{id}.wav, refs.tsv = id TAB what was said) through the live transcriber at real
/// speed, as the microphone would, with and without joining a short last piece to the one before it (order alternates
/// per recording so a busy laptop affects both alike). Reports words right and the wait after "letting go".
/// </summary>
internal static partial class Trainer
{
    public static async Task LiveReplayAsync(string[] args)
    {
        string outFile = args[1], refsPath = args[2], wavDir = args[3], model = args[4];
        var refs = File.ReadLines(refsPath).Select(l => l.Split('\t')).Where(p => p.Length >= 2 && p[1].Trim().Length > 0)
                       .Select(p => (id: p[0].Trim(), want: p[1].Trim())).Where(r => File.Exists(Path.Combine(wavDir, r.id + ".wav"))).ToList();
        var settings = AppSettings.Load();
        var (dict, snippets) = LoadUserData(settings);
        string prompt = TextCleaner.BuildPrompt(dict);
        using var engine = new SpeechEngine();
        await engine.LoadAsync(model);
        var res = new Dictionary<bool, List<(string id, int words, int err, long wait, bool merged, string text)>> { [false] = new(), [true] = new() };
        int n = 0;
        foreach (var (id, want) in refs)
        {
            var x = TrainAudio.Load(Path.Combine(wavDir, id + ".wav"));
            var order = n++ % 2 == 0 ? new[] { false, true } : new[] { true, false };
            foreach (var merge in order)
            {
                var live = new LiveTranscriber(engine, settings.Language, settings.Threads, prompt, tailMerge: merge);
                for (int i = 0; i < x.Length; i += 1600) { live.Push(x[i..Math.Min(x.Length, i + 1600)]); await Task.Delay(100); }
                var sw = Stopwatch.StartNew();
                var raw = await live.FinishAsync() ?? await SpeechGate.TranscribeAsync(engine, x, settings.Language, settings.Threads, prompt, condition: true);
                long wait = sw.ElapsedMilliseconds;
                var text = TextCleaner.Clean(raw, settings, dict, snippets);
                var sc = Score(want, text);
                res[merge].Add((id, sc.refWords, sc.err, wait, live.TailMerged, text));
                await Task.Delay(1000);
            }
        }
        static long Med(IEnumerable<long> v) { var a = v.OrderBy(t => t).ToArray(); return a.Length == 0 ? 0 : a[a.Length / 2]; }
        var md = new StringBuilder($"# Live replay of {refs.Count} recordings ({Path.GetFileName(model)})\n\n| Variant | Words right | Perfect | Median wait | Longest wait |\n|---|---:|---:|---:|---:|\n");
        foreach (var m in new[] { false, true })
        {
            var r = res[m]; int w = r.Sum(v => v.words), e = r.Sum(v => v.err);
            md.Append($"| {(m ? "short last piece joined" : "as installed before")} | {100.0 * (w - e) / Math.Max(1, w):0.0}% | {r.Count(v => v.err == 0)} of {r.Count} | {Med(r.Select(v => v.wait))} ms | {r.Max(v => v.wait)} ms |\n");
        }
        md.Append("\n## Recordings where the two differ\n\n");
        for (int i = 0; i < refs.Count; i++)
        {
            var a = res[false][i]; var b = res[true][i];
            if (a.text == b.text) continue;
            md.Append($"**{a.id}** (said: {refs[i].want})\n- before ({a.err} wrong, {a.wait} ms): {a.text}\n- joined ({b.err} wrong, {b.wait} ms{(b.merged ? "" : ", not joined")}): {b.text}\n\n");
        }
        md.Append($"\nJoined on {res[true].Count(v => v.merged)} of {refs.Count} recordings.\n");
        File.WriteAllText(outFile, md.ToString(), new UTF8Encoding(false));
    }
}
