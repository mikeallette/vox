using System.Diagnostics;
using System.Text;

namespace Vox.Core;

/// <summary>
/// Vox.exe --chunk-speed outDir model.bin manifest.tsv rounds chunk1 [chunk2 ...]
/// Compares live-mode chunk sizes fairly: each round runs every chunk size back to back on the same dictations
/// (8, 10, 15, 20 and 30 s, fed at real speed), so a busy laptop slows all sizes alike. Writes chunk-speed.md.
/// </summary>
internal static partial class Trainer
{
    public static async Task ChunkSpeedAsync(string[] args)
    {
        string outDir = args[1], model = args[2], manifest = args[3];
        int rounds = int.Parse(args[4]);
        var sizes = args.Skip(5).Select(a => double.Parse(a, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Directory.CreateDirectory(outDir);
        var progress = Path.Combine(outDir, "progress.txt");
        void P(string s) => File.AppendAllText(progress, $"{DateTime.Now:HH:mm:ss} {s}\n");
        try
        {
            var settings = AppSettings.Load();
            var (dict, snippets) = LoadUserData(settings);
            string prompt = TextCleaner.BuildPrompt(dict);
            var clips = ReadManifest(manifest).Where(c => c.Kind == "sentence").GroupBy(c => c.Id).Select(g => g.First()).ToList();
            const int R = 16000;
            using var engine = new SpeechEngine();
            await engine.LoadAsync(model);
            var lengths = new[] { 8.0, 10, 15, 20, 30 };
            // results[size][length] = list of (wait ms, words right %)
            var res = sizes.ToDictionary(s => s, _ => lengths.ToDictionary(l => l, _ => new List<(long wait, double acc)>()));
            for (int r = 0; r < rounds; r++)
                foreach (var len in lengths)
                {
                    // whole sentences until the length is reached, different sentences each round
                    var list = new List<float>(); var words = new List<string>();
                    for (int i = r * 37 + (int)len * 5; list.Count < len * R; i++)
                    { var c = clips[i % clips.Count]; list.AddRange(TrainAudio.Load(c.Wav)); list.AddRange(new float[R / 3]); words.Add(c.Written); }
                    var x = list.ToArray(); var want = string.Join(" ", words);
                    foreach (var size in sizes)
                    {
                        var live = new LiveTranscriber(engine, settings.Language, settings.Threads, prompt, size);
                        for (int i = 0; i < x.Length; i += 1600) { live.Push(x[i..Math.Min(x.Length, i + 1600)]); await Task.Delay(100); }
                        var sw = Stopwatch.StartNew();
                        var raw = await live.FinishAsync() ?? await SpeechGate.TranscribeAsync(engine, x, settings.Language, settings.Threads, prompt, condition: true);
                        long wait = sw.ElapsedMilliseconds;
                        var sc = Score(want, TextCleaner.Clean(raw, settings, dict, snippets));
                        double acc = 100.0 * (sc.refWords - sc.err) / Math.Max(1, sc.refWords);
                        res[size][len].Add((wait, acc));
                        P($"round {r + 1} {x.Length / (double)R:0.0}s chunk {size}: {wait} ms, {acc:0.0}%");
                        await Task.Delay(1500);
                    }
                }
            static long Med(List<long> v) { v.Sort(); return v[v.Count / 2]; }
            var md = new StringBuilder($"# Live chunk size: wait after letting go (median of {rounds} rounds, {Path.GetFileName(model)})\n\n");
            md.Append("| Dictation | " + string.Join(" | ", sizes.Select(s => $"{s:0} s chunk")) + " |\n|---:|" + string.Concat(sizes.Select(_ => "---:|")) + "\n");
            foreach (var len in lengths)
                md.Append($"| about {len:0} s | " + string.Join(" | ", sizes.Select(s => $"{Med(res[s][len].Select(v => v.wait).ToList())} ms")) + " |\n");
            md.Append("\nWords right (average over all dictations):\n\n| " + string.Join(" | ", sizes.Select(s => $"{s:0} s chunk")) + " |\n|" + string.Concat(sizes.Select(_ => "---:|")) + "\n| ");
            md.Append(string.Join(" | ", sizes.Select(s => $"{res[s].Values.SelectMany(v => v).Average(v => v.acc):0.0}%")) + " |\n");
            md.Append("\nWorst wait: " + string.Join(", ", sizes.Select(s => $"{s:0} s chunk {res[s].Values.SelectMany(v => v).Max(v => v.wait)} ms")) + "\n");
            File.WriteAllText(Path.Combine(outDir, "chunk-speed.md"), md.ToString(), new UTF8Encoding(false));
            P("DONE");
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(outDir, "chunk-speed.md"), "FAILED: " + ex); }
    }
}
