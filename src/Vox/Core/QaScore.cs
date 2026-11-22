using System.Text;

namespace Vox.Core;

/// <summary>
/// Vox.exe --score outFile refs.tsv hyp.tsv [hyp2.tsv ...]
/// Scores text files (id &lt;tab&gt; text) against references with the same word scoring as every other Vox test.
/// Used to measure the local language-model cleanup (tools\LlmPolish) against hand-checked reference text.
/// </summary>
internal static partial class Trainer
{
    public static void ScoreFiles(string[] args)
    {
        string outFile = args[1];
        static Dictionary<string, string> Read(string path) => File.ReadLines(path)
            .Select(l => l.Split('\t')).Where(p => p.Length >= 2 && p[1].Trim().Length > 0)
            .GroupBy(p => p[0]).ToDictionary(g => g.Key, g => g.First()[1].Trim());
        var refs = Read(args[2]);
        var md = new StringBuilder("| File | Words right | Perfect | Dictations |\n|---|---:|---:|---:|\n");
        var detail = new StringBuilder();
        foreach (var hypPath in args.Skip(3))
        {
            var hyp = Read(hypPath);
            int words = 0, err = 0, perfect = 0, n = 0;
            foreach (var (id, want) in refs)
            {
                if (!hyp.TryGetValue(id, out var got)) continue;
                var sc = Score(want, got);
                words += sc.refWords; err += sc.err; n++;
                if (sc.err == 0) perfect++;
                detail.Append($"{Path.GetFileName(hypPath)}\t{id}\t{sc.err}\t{got}\n");
            }
            md.Append($"| {Path.GetFileName(hypPath)} | {100.0 * (words - err) / Math.Max(1, words):0.0}% | {perfect} of {n} | {n} |\n");
        }
        File.WriteAllText(outFile, md.ToString(), new UTF8Encoding(false));
        File.WriteAllText(outFile + ".detail.tsv", detail.ToString(), new UTF8Encoding(false));
    }
}
