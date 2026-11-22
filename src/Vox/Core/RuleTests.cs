using System.Text;

namespace Vox.Core;

/// <summary>
/// Vox.exe --rule-tests outFile casesFile [dictionary.json]
/// QA test T1: runs every case in casesFile (tab separated: group, what the speech model wrote, what Vox must type;
/// "=" means the text must come out unchanged) through the real TextCleaner with default settings and the user's
/// dictionary, and writes a pass/fail report. Lines starting with # are comments.
/// </summary>
internal static partial class Trainer
{
    public static void RuleTests(string[] args)
    {
        string outFile = args[1], casesFile = args[2];
        var settings = new AppSettings();            // defaults, so a changed checkbox can't hide a failure
        var (dict, snippets) = LoadUserData(AppSettings.Load());
        if (args.Length > 3 && File.Exists(args[3])) dict = System.Text.Json.JsonSerializer.Deserialize<List<DictEntry>>(File.ReadAllText(args[3])) ?? new();

        var sb = new StringBuilder();
        var fails = new StringBuilder();
        var byGroup = new SortedDictionary<string, (int pass, int total)>();
        int pass = 0, total = 0;
        // personal cases (names from a private dictionary) live in an unpublished sibling file: rules.tsv -> rules.private.tsv
        var privateFile = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(casesFile)) ?? ".", Path.GetFileNameWithoutExtension(casesFile) + ".private" + Path.GetExtension(casesFile));
        var lines = File.ReadAllLines(casesFile, Encoding.UTF8).AsEnumerable();
        if (File.Exists(privateFile)) lines = lines.Concat(File.ReadAllLines(privateFile, Encoding.UTF8));
        foreach (var line in lines)
        {
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var p = line.Split('\t');
            if (p.Length < 3) continue;
            string group = p[0], input = p[1].Replace("\\n", "\n"), want = p[2] == "=" ? input : p[2].Replace("\\n", "\n");
            string got = TextCleaner.Clean(input, settings, dict, snippets);
            bool ok = got == want;
            total++; if (ok) pass++;
            var g = byGroup.GetValueOrDefault(group);
            byGroup[group] = (g.pass + (ok ? 1 : 0), g.total + 1);
            if (!ok) fails.Append($"| {group} | {Esc(input)} | {Esc(want)} | {Esc(got)} |\n");
        }
        sb.Append($"# Rule tests: {pass} of {total} pass\n\n| Group | Pass | Total |\n|---|---:|---:|\n");
        foreach (var (k, v) in byGroup) sb.Append($"| {k} | {v.pass} | {v.total} |\n");
        sb.Append(fails.Length == 0 ? "\nNo failures.\n" : $"\n## Failures\n\n| Group | Model wrote | Expected | Vox typed |\n|---|---|---|---|\n{fails}");
        File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(false));
        static string Esc(string s) => s.Replace("\n", "⏎").Replace("|", "\\|");
    }
}
