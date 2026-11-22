using System.Diagnostics;
using System.Text;

namespace Vox.Core;

/// <summary>
/// Vox.exe --qa-audio outDir model.bin manifest.tsv
/// QA tests T4 (pause test), T5 (edge audio) and T6 (speed) through the same gated paths a real dictation uses:
/// "whole" = SpeechGate.TranscribeAsync on the full clip, "live" = LiveTranscriber fed in 100 ms chunks.
/// Writes qa-audio.md. Run once per model: the first numbers are the cold start.
/// </summary>
internal static partial class Trainer
{
    public static async Task QaAudioAsync(string[] args)
    {
        string outDir = args[1], model = args[2], manifest = args[3];
        Directory.CreateDirectory(outDir);
        var md = new StringBuilder($"# Audio QA: {Path.GetFileName(model)}\n\n");
        var progress = Path.Combine(outDir, "progress.txt");
        void P(string s) => File.AppendAllText(progress, $"{DateTime.Now:HH:mm:ss} {s}\n");
        try
        {
            var settings = AppSettings.Load();
            var (dict, snippets) = LoadUserData(settings);
            string prompt = TextCleaner.BuildPrompt(dict);
            var clips = ReadManifest(manifest).Where(c => c.Kind == "sentence").GroupBy(c => c.Id).Select(g => g.First()).ToList();
            var audio = clips.ToDictionary(c => c.Id, c => TrainAudio.Load(c.Wav));
            const int R = 16000;
            static string Clean(string raw, AppSettings s, List<DictEntry> d, List<Snippet> sn) => TextCleaner.Clean(raw, s, d, sn);

            // ---- T6 speed: cold start ----
            var sw = Stopwatch.StartNew();
            using var engine = new SpeechEngine();
            await engine.LoadAsync(model);
            long loadMs = sw.ElapsedMilliseconds;
            P($"loaded in {loadMs} ms, gate {(SpeechGate.Available ? "on" : "OFF")}");
            float[] Concat(double secs, int offset)
            {
                var list = new List<float>();
                int i = offset;
                while (list.Count < secs * R) { list.AddRange(audio[clips[i % clips.Count].Id]); list.AddRange(new float[R / 3]); i++; }
                return list.Take((int)(secs * R)).ToArray();
            }
            (float[] x, string text) Whole(double secs, int offset)
            {
                var list = new List<float>(); var words = new List<string>();
                for (int i = offset; list.Count < secs * R; i++) { var c = clips[i % clips.Count]; list.AddRange(audio[c.Id]); list.AddRange(new float[R / 3]); words.Add(c.Written); }
                return (list.ToArray(), string.Join(" ", words));
            }
            string Acc(string want, string raw) { var sc = Score(want, Clean(raw, settings, dict, snippets)); return $"{100.0 * (sc.refWords - sc.err) / Math.Max(1, sc.refWords):0.0}%"; }
            sw.Restart();
            await SpeechGate.TranscribeAsync(engine, Concat(3, 0), settings.Language, settings.Threads, prompt, condition: true);
            long firstMs = sw.ElapsedMilliseconds;
            md.Append($"## T6 Speed\n\nModel load {loadMs} ms. First dictation after load (3 s of speech) {firstMs} ms.\n\n");
            md.Append("Whole-clip mode, the wait after letting go (median of 3):\n\n| Dictation length | Wait | Faster than real time |\n|---:|---:|---:|\n");
            foreach (var secs in new[] { 3.0, 10, 30, 60 })
            {
                var (x, want) = Whole(secs, (int)secs);
                var t = new List<long>(); string raw = "";
                for (int k = 0; k < 3; k++) { sw.Restart(); raw = await SpeechGate.TranscribeAsync(engine, x, settings.Language, settings.Threads, prompt, condition: true); t.Add(sw.ElapsedMilliseconds); }
                t.Sort();
                md.Append($"| {x.Length / (double)R:0} s | {t[1]} ms | {x.Length * 1000.0 / R / t[1]:0.0}x | {Acc(want, raw)} |\n");
                P($"speed whole {secs}s {t[1]} ms");
            }
            md.Append("\nLive mode (Vox's setting \"transcribe while speaking\", on by default), audio fed at real speed, wait after the last sample:\n\n| Dictation length | Wait |\n|---:|---:|\n");
            foreach (var secs in new[] { 10.0, 30, 60 })
            {
                var x = Concat(secs, 7 + (int)secs);
                var live = new LiveTranscriber(engine, settings.Language, settings.Threads, prompt);
                for (int i = 0; i < x.Length; i += 1600) { live.Push(x[i..Math.Min(x.Length, i + 1600)]); await Task.Delay(100); }
                sw.Restart();
                await live.FinishAsync();
                md.Append($"| {secs:0} s | {sw.ElapsedMilliseconds} ms |\n");
                P($"speed live {secs}s {sw.ElapsedMilliseconds} ms");
            }

            // ---- T4 pause test: speech, 5 s of quiet room, speech ----
            md.Append("\n## T4 Pause test (speech, 5 s of quiet room, speech)\n\n| Mode | Pairs | Words right | Extra words | Lost either half |\n|---|---:|---:|---:|---:|\n");
            var pauseRows = new StringBuilder();
            foreach (var mode in new[] { "whole", "live" })
            {
                int refW = 0, err = 0, extra = 0, lost = 0, pairs = 0;
                for (int i = 0; i < 12; i++)
                {
                    var a = clips[(i * 37) % clips.Count]; var b = clips[(i * 37 + 11) % clips.Count];
                    var gap = TrainAudio.AtPeak(TrainAudio.NoiseBed("room", 5 * R, 500 + i, _ => Array.Empty<float>()), 0.004f);
                    var x = audio[a.Id].Concat(gap).Concat(audio[b.Id]).ToArray();
                    string raw;
                    if (mode == "whole") raw = await SpeechGate.TranscribeAsync(engine, x, settings.Language, settings.Threads, prompt, condition: true);
                    else
                    {
                        var live = new LiveTranscriber(engine, settings.Language, settings.Threads, prompt);
                        for (int k = 0; k < x.Length; k += 1600) live.Push(x[k..Math.Min(x.Length, k + 1600)]);
                        raw = await live.FinishAsync() ?? await SpeechGate.TranscribeAsync(engine, x, settings.Language, settings.Threads, prompt, condition: true);
                    }
                    var got = Clean(raw, settings, dict, snippets);
                    var want = a.Written + " " + b.Written;
                    var sc = Score(want, got);
                    int gotW = got.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length, wantW = want.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
                    refW += sc.refWords; err += sc.err; pairs++;
                    extra += Math.Max(0, gotW - wantW);
                    if (gotW < wantW * 0.6) lost++;
                    if (sc.err > 0) pauseRows.Append($"| {mode} | {Md(want)} | {Md(got)} |\n");
                }
                md.Append($"| {mode} | {pairs} | {100.0 * (refW - err) / Math.Max(1, refW):0.0}% | {extra} | {lost} |\n");
                P($"pause {mode} done");
            }
            if (pauseRows.Length > 0) md.Append($"\nPairs with any mistake:\n\n| Mode | Should be | Vox typed |\n|---|---|---|\n{pauseRows}");

            // ---- T5 edge audio ----
            md.Append("\n## T5 Edge audio\n\n| Case | Seconds | Result | Time | Vox typed |\n|---|---:|---|---:|---|\n");
            var shortest = clips.OrderBy(c => audio[c.Id].Length).First();
            var mid = clips[clips.Count / 2];
            var cases = new List<(string name, float[] x, string want)>
            {
                ("empty (0 samples)", Array.Empty<float>(), ""),
                ("0.1 s of room noise", TrainAudio.AtPeak(TrainAudio.NoiseBed("room", R / 10, 1, _ => Array.Empty<float>()), 0.01f), ""),
                ("first 0.4 s of a sentence", audio[mid.Id][..Math.Min(audio[mid.Id].Length, (int)(0.4 * R))], "?"),
                ("shortest sentence", audio[shortest.Id], shortest.Written),
                ("clipped, 10x too loud", audio[mid.Id].Select(v => Math.Clamp(v * 10, -1f, 1f)).ToArray(), mid.Written),
                ("very quiet, peak 0.004", TrainAudio.AtPeak(audio[mid.Id], 0.004f), mid.Written),
                ("DC offset +0.2", audio[mid.Id].Select(v => v + 0.2f).ToArray(), mid.Written),
                ("5 minutes of speech", Whole(300, 50).x, Whole(300, 50).text),
            };
            foreach (var (name, x, want) in cases)
            {
                foreach (var mode in name.StartsWith("5 min") ? new[] { "whole", "live" } : new[] { "whole" })
                {
                    string result; string got = "";
                    sw.Restart();
                    try
                    {
                        string raw;
                        if (mode == "whole") raw = await SpeechGate.TranscribeAsync(engine, x, settings.Language, settings.Threads, prompt, condition: true);
                        else
                        {
                            var live = new LiveTranscriber(engine, settings.Language, settings.Threads, prompt);
                            for (int k = 0; k < x.Length; k += 1600) live.Push(x[k..Math.Min(x.Length, k + 1600)]);
                            raw = await live.FinishAsync() ?? "";
                        }
                        got = Clean(raw, settings, dict, snippets);
                        if (want == "") result = got.Length == 0 ? "OK, typed nothing" : "MADE-UP TEXT";
                        else if (want == "?") result = "no crash";
                        else if (want == "*") { int w = got.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length; result = $"no crash, {w} words"; }
                        else { var sc = Score(want, got); result = $"{100.0 * (sc.refWords - sc.err) / Math.Max(1, sc.refWords):0}% words right"; }
                    }
                    catch (Exception ex) { result = "CRASH: " + ex.GetType().Name + " " + ex.Message; }
                    var shown = got.Length > 140 ? got[..140] + "..." : got;
                    md.Append($"| {name}{(mode == "live" ? " (live)" : "")} | {x.Length / (double)R:0.0} | {result} | {sw.ElapsedMilliseconds} ms | {Md(shown)} |\n");
                    P($"edge {name} {mode}: {result}");
                }
            }
            md.Append($"\nProcess peak memory: {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576} MB.\n");
            File.WriteAllText(Path.Combine(outDir, "qa-audio.md"), md.ToString(), new UTF8Encoding(false));
            P("DONE");
        }
        catch (Exception ex) { P("FAILED: " + ex); File.WriteAllText(Path.Combine(outDir, "qa-audio.md"), md + "\n\nFAILED: " + ex, new UTF8Encoding(false)); }
    }
}
