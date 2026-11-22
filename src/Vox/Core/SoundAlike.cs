using System.Text;
using System.Text.RegularExpressions;

namespace Vox.Core;

/// <summary>
/// Sound-alike matching for dictionary terms (added 2026-11-08). A dictionary entry with only "write as" filled
/// (a term like "derating" or "MOSFET") also catches spellings that sound the same: "derotting", "Drotting",
/// "Moss FET", "data sheet". The test loop showed the model misspells one term many different ways, and an exact
/// "heard as" rule only catches one of them.
/// Guard rails, so ordinary speech is never touched:
///   - one word: the term must have at least 4 sounds, the word must not be an everyday word, and it must be spelled
///     close to the term
///   - two or three words run together ("Moss FET"): spelled within 2 letters of the term, first word not an
///     everyday word ("to rate" never becomes "derate"), no punctuation between them
/// </summary>
internal static class SoundAlike
{
    sealed record Term(string Text, string Letters, string Key);
    static readonly Regex Word = new(@"[\p{L}][\p{L}\d']*", RegexOptions.Compiled);

    // The ~300 most common English words plus a few dictation ones; a word on this list is never "corrected".
    static readonly HashSet<string> Everyday = new((
        "a about above after again against all almost also always am an and another any anything are around as ask at away back be because been before " +
        "being below best better between big both but by call came can come could day did different do does done down during each early end enough even " +
        "every far feel few find first for found from full get give go going good got great had has have having he her here him his home house how i if " +
        "in into is it its just keep kind know last late leave left less let life like line little long look lot made make man many may me mean might " +
        "more most move much must my need never new next no not nothing now number of off often old on once one only open or other our out over own " +
        "part people place put rather read real really right room run said same saw say see seem set she should show side since small so some " +
        "something soon start still such sure take tell than that the their them then there these they thing things think this those though " +
        "thought three through time to today together told too took top total try turn two under until up us use used very want was water way we " +
        "well went were what when where which while who whole why will with without word work world would write year yes yet you young your " +
        "rate rated rates rating treat treated treating trade trading rotting rotate rotated dirt dirty most fit fits fat " +
        "rain rains rained raining rainy reign reins rein train trains brain brains ran rang " +
        "ports port boards board sports bomb bombs boom bump mosque mascot").Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

    static readonly HashSet<string> Function = new(StringComparer.OrdinalIgnoreCase)
    {
        "a","an","the","and","or","but","to","of","in","on","at","for","is","it","its","be","as","by","we","i","you","he","she","they","this","that","so","if","with","from","are","was","were","not","no","do","can","will","have","has","had","me","my","our","your","up","out",
    };

    static List<Term> _terms = new();
    static string _sig = "";

    /// <summary>Applies sound-alike matching for the term-only entries of the dictionary.</summary>
    public static string Apply(string t, IEnumerable<DictEntry> dict)
    {
        var terms = Terms(dict);
        if (terms.Count == 0 || t.Length == 0) return t;
        var words = Word.Matches(t).Cast<Match>().ToList();
        var sb = new StringBuilder();
        int pos = 0;
        for (int i = 0; i < words.Count;)
        {
            var hit = Find(t, words, i, terms);
            if (hit == null) { i++; continue; }
            var (n, term, suffix) = hit.Value;
            int start = words[i].Index, end = words[i + n - 1].Index + words[i + n - 1].Length;
            var text = term.Text + suffix;
            if (char.IsLower(text[0]) && (char.IsUpper(t[start]) && AtSentenceStart(t, start))) text = char.ToUpperInvariant(text[0]) + text[1..];
            sb.Append(t, pos, start - pos).Append(text);
            pos = end; i += n;
        }
        if (pos == 0) return t;
        sb.Append(t, pos, t.Length - pos);
        return sb.ToString();
    }

    static (int n, Term term, string suffix)? Find(string t, List<Match> words, int i, List<Term> terms)
    {
        for (int n = Math.Min(3, words.Count - i); n >= 1; n--)
        {
            // words must be next to each other, separated only by spaces or a hyphen
            bool joined = true;
            for (int k = i; k < i + n - 1; k++)
            {
                var gap = t[(words[k].Index + words[k].Length)..words[k + 1].Index];
                if (gap.Length == 0 || gap.Length > 2 || gap.Any(c => c != ' ' && c != '-')) { joined = false; break; }
            }
            if (!joined) continue;
            var parts = words.Skip(i).Take(n).Select(w => w.Value).ToList();
            if (n == 1 && Everyday.Contains(parts[0])) continue;
            if (n > 1 && Everyday.Contains(parts[0]) && parts[0].Length > 1 && !char.IsUpper(parts[0][^1])) continue;
            if (n > 1 && parts.Any(p => Function.Contains(p))) continue;          // "data sheet to" must never swallow "to"
            var cand = Letters(string.Concat(parts));
            foreach (var (stem, suffix) in Stems(cand, parts[^1]))
                foreach (var term in terms)
                {
                    if (stem == term.Letters) { if (n == 1) continue; }      // exact single word: the plain dictionary already handled it
                    if (Key(stem) != term.Key) continue;
                    int dist = Edit(stem, term.Letters);
                    if (n == 1 && (term.Key.Length < 4 || dist > Math.Max(2, term.Letters.Length / 2))) continue;
                    if (n > 1 && (term.Letters.Length < 5 || dist > (n == 2 ? 2 : 1))) continue;
                    return (n, term, suffix);
                }
        }
        return null;
    }

    static IEnumerable<(string stem, string suffix)> Stems(string letters, string lastWord)
    {
        yield return (letters, "");
        if (lastWord.EndsWith("'s", StringComparison.OrdinalIgnoreCase) && letters.EndsWith("s")) yield return (letters[..^1], "'s");
        else if (letters.Length > 4 && letters.EndsWith("s")) yield return (letters[..^1], "s");
    }

    static List<Term> Terms(IEnumerable<DictEntry> dict)
    {
        var list = dict.Where(d => string.IsNullOrWhiteSpace(d.From) && !string.IsNullOrWhiteSpace(d.To) && !d.To.Contains(' ')).Select(d => d.To.Trim()).ToList();
        var sig = string.Join("\u0001", list);
        if (sig == _sig) return _terms;
        _terms = list.Where(x => Letters(x).Length >= 4).Select(x => new Term(x, Letters(x), Key(Letters(x)))).ToList();
        _sig = sig;
        return _terms;
    }

    static bool AtSentenceStart(string t, int i)
    {
        int j = i - 1;
        while (j >= 0 && (t[j] == ' ' || t[j] == '"')) j--;
        return j < 0 || t[j] is '.' or '!' or '?' or '\n';
    }

    static string Letters(string s) => new(s.ToLowerInvariant().Where(char.IsLetter).ToArray());

    /// <summary>A rough "how it sounds" key: consonant sounds only, similar sounds merged (d/t, g/k, c/k/s, v/f, ph/f).</summary>
    public static string Key(string letters)
    {
        var s = letters.ToLowerInvariant();
        s = s.Replace("ph", "f").Replace("ck", "k").Replace("sh", "x").Replace("ch", "x").Replace("th", "0").Replace("qu", "k").Replace("gh", "");
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i], next = i + 1 < s.Length ? s[i + 1] : '\0';
            char m = c switch
            {
                'c' => next is 'e' or 'i' or 'y' ? 's' : 'k',
                'q' => 'k', 'z' => 's', 'd' => 't', 'g' => 'k', 'v' => 'f', 'x' => 'x',
                'a' or 'e' or 'i' or 'o' or 'u' or 'y' or 'h' or 'w' => i == 0 && c is 'a' or 'e' or 'i' or 'o' or 'u' ? 'a' : '\0',
                _ => c,
            };
            if (m == '\0') continue;
            if (sb.Length > 0 && sb[^1] == m) continue;
            sb.Append(m);
        }
        return sb.ToString();
    }

    static int Edit(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }
}
