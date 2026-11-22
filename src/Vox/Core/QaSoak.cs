using System.Diagnostics;
using System.Text;

namespace Vox.Core;

/// <summary>
/// Vox.exe --soak outDir model.bin manifest.tsv minutes [gapSeconds]
/// QA test T10: one dictation after another (live path, audio fed at real speed, then the gap) for the given time,
/// sampling memory, handles and the wait after each dictation. Writes soak.tsv and soak.md.
/// </summary>
internal static partial class Trainer
{
    public static async Task SoakAsync(string[] args)
    {
        string outDir = args[1], model = args[2], manifest = args[3];
        double minutes = double.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture);
        int gap = args.Length > 5 ? int.Parse(args[5]) : 20;
        Directory.CreateDirectory(outDir);
        var tsvPath = Path.Combine(outDir, "soak.tsv");
        File.WriteAllText(tsvPath, "n\tminute\taudio_s\twait_ms\tworking_mb\tprivate_mb\thandles\tthreads\twords_right\n");
        try
        {
            var settings = AppSettings.Load();
            var (dict, snippets) = LoadUserData(settings);
            string prompt = TextCleaner.BuildPrompt(dict);
            var clips = ReadManifest(manifest).Where(c => c.Kind == "sentence").ToList();
            using var engine = new SpeechEngine();
            await engine.LoadAsync(model);
            var start = Stopwatch.StartNew();
            var rng = new Random(42);
            var rows = new List<(double min, long wait, double ws, double priv, int handles, double acc)>();
            int n = 0;
            while (start.Elapsed.TotalMinutes < minutes)
            {
                n++;
                // 1 to 4 sentences in a row, sometimes over the 12 s live chunk size
                var pick = Enumerable.Range(0, 1 + rng.Next(4)).Select(_ => clips[rng.Next(clips.Count)]).ToList();
                var x = pick.SelectMany(c => TrainAudio.Load(c.Wav).Concat(new float[16000 / 3])).ToArray();
                var want = string.Join(" ", pick.Select(c => c.Written));
                var live = new LiveTranscriber(engine, settings.Language, settings.Threads, prompt);
                for (int i = 0; i < x.Length; i += 1600) { live.Push(x[i..Math.Min(x.Length, i + 1600)]); await Task.Delay(100); }
                var sw = Stopwatch.StartNew();
                var raw = await live.FinishAsync() ?? await SpeechGate.TranscribeAsync(engine, x, settings.Language, settings.Threads, prompt, condition: true);
                long wait = sw.ElapsedMilliseconds;
                var sc = Score(want, TextCleaner.Clean(raw, settings, dict, snippets));
                double acc = 100.0 * (sc.refWords - sc.err) / Math.Max(1, sc.refWords);
                var p = Process.GetCurrentProcess(); p.Refresh();
                double ws = p.WorkingSet64 / 1048576.0, priv = p.PrivateMemorySize64 / 1048576.0;
                rows.Add((start.Elapsed.TotalMinutes, wait, ws, priv, p.HandleCount, acc));
                File.AppendAllText(tsvPath, $"{n}\t{start.Elapsed.TotalMinutes:0.0}\t{x.Length / 16000.0:0.0}\t{wait}\t{ws:0}\t{priv:0}\t{p.HandleCount}\t{p.Threads.Count}\t{acc:0.0}\n");
                await Task.Delay(gap * 1000);
            }
            // compare the first and last fifth of the run
            int k = Math.Max(1, rows.Count / 5);
            var first = rows.Take(k).ToList(); var last = rows.TakeLast(k).ToList();
            static double Med(IEnumerable<double> v) { var l = v.OrderBy(a => a).ToList(); return l[l.Count / 2]; }
            var md = new StringBuilder($"# Soak test: {n} dictations over {start.Elapsed.TotalMinutes:0} minutes ({Path.GetFileName(model)})\n\n");
            md.Append("| | First fifth | Last fifth | Change |\n|---|---:|---:|---:|\n");
            void Row(string name, Func<(double min, long wait, double ws, double priv, int handles, double acc), double> f, string unit)
            {
                double a = Med(first.Select(f)), b = Med(last.Select(f));
                md.Append($"| {name} | {a:0.#}{unit} | {b:0.#}{unit} | {(a == 0 ? 0 : 100 * (b - a) / a):+0;-0;0}% |\n");
            }
            Row("Private memory (median)", r => r.priv, " MB");
            Row("Working set (median)", r => r.ws, " MB");
            Row("Handles (median)", r => r.handles, "");
            Row("Wait after letting go (median)", r => r.wait, " ms");
            Row("Words right (median)", r => r.acc, "%");
            md.Append($"\nPeak private memory {rows.Max(r => r.priv):0} MB. Worst wait {rows.Max(r => r.wait)} ms. Overall words right {rows.Average(r => r.acc):0.0}%.\n");
            File.WriteAllText(Path.Combine(outDir, "soak.md"), md.ToString(), new UTF8Encoding(false));
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(outDir, "soak.md"), "FAILED: " + ex); }
    }
}
