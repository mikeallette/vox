using System.Text.RegularExpressions;

namespace Vox.Core;

/// <summary>
/// "Teach a word": compares what Vox typed with the user's corrected version and proposes dictionary entries for the
/// words that changed ("Bryan has a dentist" -> "Brian has a dentist" proposes Bryan -> Brian). A change on a short,
/// everyday word keeps one neighbouring word so the entry only fires in that context. Removed words are skipped.
/// </summary>
internal static class TeachWords
{
    static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "to", "of", "in", "on", "at", "for", "by", "and", "or", "is", "it", "be", "as", "so", "we", "i",
        "you", "he", "she", "they", "my", "our", "your", "that", "this", "with", "are", "was", "were", "do", "no", "not",
        "up", "if", "me", "us", "can", "will", "all", "but", "from", "have", "has", "had", "its", "it's", "one", "two",
    };

    static string Bare(string w) => w.Trim('.', ',', ';', ':', '!', '?', '"', '(', ')');

    public static List<DictEntry> Propose(string typed, string corrected)
    {
        var a = Regex.Split(typed.Trim(), @"\s+").Where(w => w.Length > 0).ToArray();
        var b = Regex.Split(corrected.Trim(), @"\s+").Where(w => w.Length > 0).ToArray();
        var outp = new List<DictEntry>();
        if (a.Length == 0 || b.Length == 0) return outp;

        // longest common subsequence on bare words (punctuation and case differences alone don't count as a change)
        int n = a.Length, m = b.Length;
        var L = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                L[i, j] = Bare(a[i]) == Bare(b[j]) ? L[i + 1, j + 1] + 1 : Math.Max(L[i + 1, j], L[i, j + 1]);

        // walk the alignment; each run of non-matching words is one change (a[x0..x] became b[y0..y])
        var spans = new List<(int x0, int x1, int y0, int y1)>();
        int x = 0, y = 0, sx = -1, sy = -1;
        while (x < n || y < m)
        {
            if (x < n && y < m && Bare(a[x]) == Bare(b[y]))
            {
                if (sx >= 0) { spans.Add((sx, x, sy, y)); sx = -1; }
                x++; y++; continue;
            }
            if (sx < 0) { sx = x; sy = y; }
            if (y >= m || (x < n && L[x + 1, y] >= L[x, y + 1])) x++; else y++;
        }
        if (sx >= 0) spans.Add((sx, n, sy, m));

        foreach (var (x0, x1, y0, y1) in spans)
        {
            if (y1 == y0) continue;                                  // words removed: not a dictionary job
            // swapping one everyday word for another ("a" -> "the") would fire all over ordinary speech: skip it
            if (x1 - x0 <= 1 && y1 - y0 == 1 && (x1 == x0 || Common.Contains(Bare(a[x0]))) && Common.Contains(Bare(b[y0]))) continue;
            int fa = x0, ta = x1, fb = y0, tb = y1;
            // a lone everyday word (or a pure insertion) needs context: take the word before it, else the one after
            bool weak = ta - fa == 0 || (ta - fa == 1 && Common.Contains(Bare(a[fa])));
            if (weak)
            {
                // an added word takes the words on both sides, so "LLC" -> "LLC tank" can't turn "LLC tank" into "LLC tank tank"
                bool inserted = ta == fa;
                if (fa > 0 && fb > 0) { fa--; fb--; }
                else if (ta < n && tb < m) { ta++; tb++; }
                if (inserted && ta < n && tb < m && ta - fa < 2) { ta++; tb++; }
            }
            var from = Bare(string.Join(" ", a[fa..ta]));
            var to = Bare(string.Join(" ", b[fb..tb]));
            if (from.Length == 0 || to.Length == 0 || from == to) continue;
            if (from.Split(' ').Length > 6) continue;                // a rewritten sentence, not a misheard word
            if (!outp.Any(e => e.From == from)) outp.Add(new DictEntry { From = from, To = to });
        }
        return outp;
    }
}
