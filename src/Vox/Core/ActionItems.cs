using System.Text;
using System.Text.RegularExpressions;

namespace Vox.Core;

/// <summary>
/// Pulls likely action items out of a finished meeting transcript with plain rules (no AI): lines where someone
/// commits ("I'll send...", "we need to..."), asks ("can you check..."), or says "action item". Each is listed with its
/// time and speaker so it can be checked against the transcript. It is a starting list, not a summary.
/// </summary>
internal static class ActionItems
{
    static readonly Regex Cue = new(
        @"\b(?:I'll|I will|I'm going to|I can take|let me|we'll|we will|we need to|we should|we have to|you'll|" +
        @"can you|could you|would you|will you|please|make sure|don't forget|remember to|action item|follow up|follow-up|" +
        @"by (?:monday|tuesday|wednesday|thursday|friday|saturday|sunday|tomorrow|end of (?:the )?(?:day|week))|next step)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // polite filler that matches a cue but isn't a task
    static readonly Regex NotATask = new(@"^\W*(?:let me (?:know|see|think|check if you can hear)|can you (?:hear|see) me|could you repeat|please hold|you'll see)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Regex Sentence = new(@"[^.!?]+[.!?]?", RegexOptions.Compiled);

    public static List<(double start, string speaker, string text)> Find(IEnumerable<(double start, string speaker, string text)> lines, int max = 25)
    {
        var found = new List<(double, string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (start, speaker, text) in lines)
            foreach (Match m in Sentence.Matches(text))
            {
                var s = m.Value.Trim();
                int words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                if (words < 4 || words > 40 || !Cue.IsMatch(s) || NotATask.IsMatch(s)) continue;
                if (!seen.Add(s)) continue;
                found.Add((start, speaker, s));
                if (found.Count >= max) return found;
            }
        return found;
    }

    public static string Markdown(List<(double start, string speaker, string text)> items, Func<double, string> ts)
    {
        if (items.Count == 0) return "";
        var sb = new StringBuilder("## Possible action items\n\n_Found by simple rules from what was said. Check each one against the transcript._\n\n");
        foreach (var (start, speaker, text) in items) sb.Append($"- [ ] {text} _({speaker}, {ts(start)})_\n");
        return sb.Append('\n').ToString();
    }
}
