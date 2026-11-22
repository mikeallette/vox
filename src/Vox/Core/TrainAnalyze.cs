using System.Text;

namespace Vox.Core;

/// <summary>
/// Vox.exe --analyze resultsDir outDir
/// Re-cleans every saved test result with the current TextCleaner and dictionary (no audio needed, so a rule change
/// can be measured in seconds) and writes:
///   score.md     words right / perfect, by category and condition (acoustic conditions listed apart)
///   errors.tsv   every remaining mistake, grouped: what should have been typed -> what Vox typed, how often
/// </summary>
internal static partial class Trainer
{
    static readonly HashSet<string> Acoustic = new(StringComparer.OrdinalIgnoreCase) { "far-room", "office", "tv" };

    /// <summary>Vox.exe --clean outFile inFile [dictionary.json]: each line of inFile, cleaned (for checking rules by hand).</summary>
    public static void CleanFile(string[] args)
    {
        var settings = AppSettings.Load();
        var (dict, snippets) = LoadUserData(settings);
        if (args.Length > 3 && File.Exists(args[3])) dict = System.Text.Json.JsonSerializer.Deserialize<List<DictEntry>>(File.ReadAllText(args[3])) ?? new();
        File.WriteAllLines(args[1], File.ReadAllLines(args[2]).Select(l => l + "\n   -> " + TextCleaner.Clean(l, settings, dict, snippets).Replace("\n", " / ")), new UTF8Encoding(false));
    }

    public static void Analyze(string[] args)
    {
        string dir = args[1], outDir = args[2];
        Directory.CreateDirectory(outDir);
        var settings = AppSettings.Load();
        var (dict, snippets) = LoadUserData(settings);
        if (args.Length > 3 && File.Exists(args[3]))      // an alternative dictionary to measure
            dict = System.Text.Json.JsonSerializer.Deserialize<List<DictEntry>>(File.ReadAllText(args[3])) ?? new();
        var results = Directory.GetFiles(dir, "results-*.jsonl").SelectMany(ReadResults)
            .Where(r => !r.NoSound && r.Kind == "sentence").ToList();
        foreach (var r in results)
        {
            r.Category = System.Text.RegularExpressions.Regex.Replace(r.Category, "^[^-]+-(?=[a-z])", "");
            r.Clean = TextCleaner.Clean(r.Raw, settings, dict, snippets);
            (r.RefWords, r.Err, r.ErrCs) = Score(r.Written, r.Clean);
        }

        var md = new StringBuilder($"# Re-scored {results.Count} dictations with {dict.Count} dictionary rules\n\n");
        void Table(string title, IEnumerable<IGrouping<string, Result>> groups)
        {
            md.Append($"## {title}\n\n| | Dictations | Words right | Perfect |\n|---|---:|---:|---:|\n");
            foreach (var g in groups.OrderByDescending(g => Pct(g)))
                md.Append($"| {g.Key} | {g.Count()} | {Pct(g):0.0}% | {100.0 * g.Count(r => r.ErrCs == 0) / g.Count():0.0}% |\n");
            md.Append('\n');
        }
        static double Pct(IEnumerable<Result> g) { int w = g.Sum(r => r.RefWords), e = g.Sum(r => r.ErrCs); return 100.0 * (w - e) / Math.Max(1, w); }
        var normal = results.Where(r => !Acoustic.Contains(r.Condition)).ToList();
        md.Append($"Overall (normal conditions): {Pct(normal):0.0}% words right, {100.0 * normal.Count(r => r.ErrCs == 0) / Math.Max(1, normal.Count):0.0}% perfect. ");
        md.Append($"All conditions: {Pct(results):0.0}% / {100.0 * results.Count(r => r.ErrCs == 0) / Math.Max(1, results.Count):0.0}%.\n\n");
        Table("By kind of sentence (normal conditions)", normal.GroupBy(r => r.Category));
        Table("By condition", results.GroupBy(r => r.Condition));
        Table("By mode (normal conditions)", normal.GroupBy(r => r.Mode));
        File.WriteAllText(Path.Combine(outDir, "score.md"), md.ToString(), new UTF8Encoding(false));

        // error regions, normal conditions only (acoustic ones are not a vocabulary problem)
        var map = new Dictionary<string, (int n, HashSet<string> cats, string example)>();
        foreach (var r in normal.Where(r => r.ErrCs > 0))
        {
            var R = Tokenize(r.Written); var H = Tokenize(r.Clean);
            var ops = Align(R, H);
            int i = 0;
            while (i < ops.Count)
            {
                if (ops[i].op == Op.Eq) { i++; continue; }
                int j = i; while (j < ops.Count && ops[j].op != Op.Eq) j++;
                var rs = ops.Skip(i).Take(j - i).Where(o => o.r >= 0 && o.op != Op.Ins).Select(o => R[o.r].Cased);
                var hs = ops.Skip(i).Take(j - i).Where(o => o.h >= 0 && o.op != Op.Del).Select(o => H[o.h].Cased);
                var key = string.Join(" ", rs) + "\t" + string.Join(" ", hs);
                if (!map.TryGetValue(key, out var v)) v = (0, new HashSet<string>(), Trim(r.Clean));
                v.n++; v.cats.Add(r.Category); map[key] = v;
                i = j;
            }
        }
        var tsv = new StringBuilder("count\tshould be\tVox typed\tcategories\texample\n");
        foreach (var kv in map.OrderByDescending(k => k.Value.n))
            tsv.Append($"{kv.Value.n}\t{kv.Key}\t{string.Join(",", kv.Value.cats)}\t{kv.Value.example}\n");
        File.WriteAllText(Path.Combine(outDir, "errors.tsv"), tsv.ToString(), new UTF8Encoding(false));
    }
}
