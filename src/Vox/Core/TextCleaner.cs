using System.Text.RegularExpressions;

namespace Vox.Core;

/// <summary>
/// Rule-based, fully offline cleanup. It stays predictable on purpose: it only removes obvious
/// non-speech, filler sounds and applies the user's own dictionary, snippets and spoken commands.
/// </summary>
public static class TextCleaner
{
    // [BLANK_AUDIO], (music), *noise*, ♪ ... : whisper non-speech markers
    static readonly Regex NonSpeech = new(@"\[[^\]]{0,40}\]|\((?:music|silence|noise|applause|laughter|inaudible|blank[_ ]audio)[^)]*\)|\*[^*]{0,30}\*|[♪♫]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    const string Fillers = @"(?:um+|uh+|uhm+|erm+|hmm+|mhm+|uh-huh)";
    static readonly Regex FillerAtSentenceStart = new($@"(^|[.!?]\s+){Fillers}\b[,.;]?\s*([a-z])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex FillerAnywhere = new($@"[ \t]*\b{Fillers}\b[,;]?(?=\s|$|[.!?])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Spoken stumbles, as Wispr Flow tidies them (real recordings, 2026-11-09): "I I should", "on on my laptop" -> one word.
    // Only small words people stumble on; "had had", "that that", "so so", "very very" are left alone.
    static readonly Regex RepeatWord = new(@"\b(\p{L}[\p{L}']{2,})(?:[,.]?[ \t]+\1\b)+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly HashSet<string> KeepRepeat = new(StringComparer.OrdinalIgnoreCase)
    {
        "had", "that", "bye", "very", "really", "yeah", "yes", "okay", "please", "knock", "there", "well", "hey", "now", "never",
        "more", "again", "over", "round", "around", "far", "long", "much", "wait", "come", "hush", "tick", "tock", "beep", "ding",
        "bang", "boom", "chop", "wow", "ooh", "aah", "haha", "hip", "blah", "nah", "yay", "choo", "pretty", "super", "too", "out",
        "down", "back", "and", "on", "off", "ever", "bit", "step", "side", "one", "two", "three", "four", "five", "six", "seven",
        "eight", "nine", "ten", "night", "dear", "poor", "fast", "slow", "high", "low", "big", "little", "tiny", "hot", "cold",
        "hello", "help", "stop", "right", "test", "testing", "check",   // "Hello, hello, hello" is a mic check, not a stutter
    };
    static readonly Regex ShortRestart = new(@"\b(no|not|the|a|an|to|on|in|my|our|your|for|with|is|it's|at|of|by|this|that)[ \t]+(\p{L}{3,})[,.]?[ \t]+\1[ \t]+(\p{L}{3,})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Stutter =new(@"\b(i|the|a|an|to|on|in|of|and|it|we|he|she|they|you|my|for|with|at|but|or|if|can|will|was|be|is)(?:[ \t]+\1\b)+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // A restart: "I should be able to only download I should be able to download" -> "I should be able to download".
    // The same 4+ words said again within 3 words; not when a joining word sits between ("... and I want to go home").
    static readonly Regex FalseStart = new(@"\b(?<p>\w+(?:[ \t]+\w+){3,})(?<mid>(?:[ \t]+[\w']+){0,3}?)[ \t]+(?<p2>\k<p>)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // The speech model often writes the command as one word ("newline", "Newline?"), so accept both spellings.
    // Not a command when it is part of a sentence: "the new line of products", "a new paragraph about..." (QA T1, 2026-11-09)
    const string NotCommandBefore = @"(?<!\b(?:the|a|an|our|this|that|their|his|her|my|your)\s+)";
    const string NotCommandAfter = @"(?![ \t]+(?:of|for|about|from|in|on|items?|products?)\b)";
    static readonly Regex NewParagraph = new($@"\s*{NotCommandBefore}\bnew[ -]?paragraph\b{NotCommandAfter}[.,!?]?\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex NewLine = new($@"\s*{NotCommandBefore}\bnew[ -]?line\b{NotCommandAfter}[.,!?]?\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Times: "910am", "10.30am", "8:15 pm" -> "9:10 a.m.", "10:30 a.m.", "8:15 p.m.";  "11am" -> "11 a.m."
    static readonly Regex ClockTime = new(@"\b(1[0-2]|0?[1-9])[:.]?([0-5]\d) ?([ap])\.? ?m\b\.?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex HourTime = new(@"\b(1[0-2]|[1-9]) ?([ap])\.? ?m\b\.?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Units written onto the number: "4mm", "300mV", "250kHz" -> "4 mm", "300 mV", "250 kHz"
    static readonly Regex GluedUnit = new(@"\b(\d+(?:\.\d+)?)(mm|mV|mA|kHz|MHz|kV|kΩ|ms)\b", RegexOptions.Compiled);
    // "pull up on The I2C" -> "on the I2C": the model sometimes capitalizes a small word in the middle of a sentence
    static readonly Regex MidCap = new(@"(?<=\b\p{Ll}[\p{L}\d']*,? )(The|A|An|And|Of|On|In|At|To|For|With|By|Or|But|From|Is|Are)\b(?= \p{Ll}| \p{Lu}[\p{Lu}\d])", RegexOptions.Compiled);
    // "Hi mark," -> "Hi Mark,"
    static readonly Regex Greeting = new(@"\b(Hi|Hello|Hey|Dear) (?!(?:all|everyone|everybody|team|guys|folks|there|both|friends|again)\b)(\p{Ll})", RegexOptions.Compiled);
    static readonly Regex HyGreeting = new(@"(^|[.!?]\s+|\n)Hy (?=\p{Lu})", RegexOptions.Compiled);   // "Hy Priya," -> "Hi Priya,"
    static readonly Regex AfterBreak = new(@"(\n[ \t]*)(\p{Ll})", RegexOptions.Compiled);
    static readonly Regex MultiSpace = new(@"[ \t]{2,}", RegexOptions.Compiled);
    // a whole word (2+ letters) in Cyrillic, Greek, Armenian, Hebrew, Arabic, Indic, Thai, Georgian, Hangul or CJK, with its trailing punctuation
    const string NonLatinLetter = @"[Ͱ-ϿЀ-ԯ԰-֏֐-׿؀-ۿऀ-෿฀-๿Ⴀ-ჿ぀-ヿ㐀-鿿가-힯]";
    static readonly Regex ForeignWord = new($@"(?<![\w])(?:{NonLatinLetter}){{2,}}[.,!?;:]?(?![\w])", RegexOptions.Compiled);
    // ". " then a lowercase word, after a real word (not a.m., e.g., etc., vs., No., or an initial)
    static readonly Regex StrayPeriod = new(@"(?<=[\p{L}\d°%Ω]{2,})(?<!\b(?:[ap]\.m|e\.g|i\.e|etc|vs|approx|incl|Mr|Mrs|Ms|Dr|St|No|Fig|Rev))\. (?=\p{Ll})", RegexOptions.Compiled);

    /// <summary>Test tools only: VOX_OLD_RULES=1 scores with the cleanup as it was before 2026-11-08 (for before/after comparisons).</summary>
    static bool OldRulesForTesting => Environment.GetEnvironmentVariable("VOX_OLD_RULES") == "1";

    public static string Clean(string raw, AppSettings s, IEnumerable<DictEntry> dict, IEnumerable<Snippet> snippets)
    {
        var t = raw ?? "";
        t = NonSpeech.Replace(t, " ");
        // The speech model knows 25 languages and can't be locked to English, so a mumble can come out as a word in
        // another alphabet ("Безмарой.", 2026-11-11). In an English dictation, drop whole words written in another
        // alphabet. Unit symbols (Ω, µ) are single letters next to Latin letters or digits, so they stay.
        if (Regex.Matches(t, @"\p{IsBasicLatin}").Count > 0 && Regex.IsMatch(t, "[A-Za-z]{2,}"))
            t = ForeignWord.Replace(t, " ");
        t = t.Replace(' ', ' ').Trim();
        if (t.Length == 0) return "";

        if (s.RemoveFillers)
        {
            t = FillerAtSentenceStart.Replace(t, m => m.Groups[1].Value + m.Groups[2].Value.ToUpperInvariant());
            t = FillerAnywhere.Replace(t, "");
            t = Stutter.Replace(t, "$1");
            // any longer word said twice in a row ("authentication authentication"), unless people repeat it on purpose
            t = RepeatWord.Replace(t, m => KeepRepeat.Contains(m.Groups[1].Value) ? m.Value : m.Groups[1].Value);
            // a two-word restart on a near-miss word: "no running, no ringing" -> "no ringing" (script read 2026-11-11)
            t = ShortRestart.Replace(t, m =>
            {
                string x = m.Groups[2].Value, y = m.Groups[3].Value;
                bool nearMiss = !x.Equals(y, StringComparison.OrdinalIgnoreCase) && char.ToLowerInvariant(x[0]) == char.ToLowerInvariant(y[0])
                                && Math.Abs(x.Length - y.Length) <= 3 && x.Length >= 4 && y.Length >= 4
                                && x[^2..].Equals(y[^2..], StringComparison.OrdinalIgnoreCase);   // same start and ending: a near-miss, not a list ("the board, the box")
                return nearMiss ? m.Groups[1].Value + " " + y : m.Value;
            });
            t = FalseStart.Replace(t, m => Regex.IsMatch(m.Groups["mid"].Value, @"\b(?:and|or|but|then|so|because)\b", RegexOptions.IgnoreCase) ? m.Value : m.Groups["p2"].Value);
        }

        bool newRules = !OldRulesForTesting;
        if (s.FormatNumbers && newRules) t = NumberFormat.Apply(t);
        t = ClockTime.Replace(t, m => $"{m.Groups[1].Value.TrimStart('0')}:{m.Groups[2].Value} {char.ToLowerInvariant(m.Groups[3].Value[0])}.m.");
        t = HourTime.Replace(t, m => $"{m.Groups[1].Value} {char.ToLowerInvariant(m.Groups[2].Value[0])}.m.");
        t = GluedUnit.Replace(t, "$1 $2");

        // Snippets first (spoken trigger -> expansion), then the dictionary.
        foreach (var sn in snippets)
        {
            if (string.IsNullOrWhiteSpace(sn.Trigger)) continue;
            var rx = new Regex($@"\b{Regex.Escape(sn.Trigger.Trim())}\b[.,!?]?", RegexOptions.IgnoreCase);
            t = rx.Replace(t, _ => sn.Text.Replace("\r\n", "\n"));
        }
        foreach (var d in dict)
        {
            if (string.IsNullOrWhiteSpace(d.To)) continue;
            var heard = string.IsNullOrWhiteSpace(d.From) ? d.To : d.From;
            // (?!-\w): "Google sign" must not touch "Google sign-in" (it would become "sign-in-in")
            var rx = new Regex($@"(?<![\w]){Regex.Escape(heard.Trim())}(?![\w]|-\w)", RegexOptions.IgnoreCase);
            t = newRules ? rx.Replace(t, m => KeepSentenceCap(t, m, d.To)) : rx.Replace(t, d.To);
        }
        if (newRules) t = SoundAlike.Apply(t, dict);

        if (s.VoiceCommands)
        {
            t = NewParagraph.Replace(t, "\n\n");
            t = NewLine.Replace(t, "\n");
            // "Hi Mark, new line, thanks for..." -> the new line starts with a capital, like typing it would
            t = AfterBreak.Replace(t, m => m.Groups[1].Value + m.Groups[2].Value.ToUpperInvariant());
        }

        if (newRules)
        {
            // "on the gate. and a 4.7 µF cap": the model put a period at a pause but carried on in lowercase (script read 2026-11-11)
            t = StrayPeriod.Replace(t, " ");
            t = MidCap.Replace(t, m => m.Value.ToLowerInvariant());
            t = HyGreeting.Replace(t, "$1Hi ");
            t = Greeting.Replace(t, m => m.Groups[1].Value + " " + m.Groups[2].Value.ToUpperInvariant());
        }
        // A dictation that starts with "new paragraph" / "new line" keeps that break (it used to be trimmed away).
        var lead = Regex.Match(t, @"^[ \t]*(\n+)").Groups[1].Value;
        t = MultiSpace.Replace(t, " ").Trim();
        // Never start a result with a lowercase letter after cleanup removed leading words.
        if (t.Length > 0 && char.IsLower(t[0])) t = char.ToUpperInvariant(t[0]) + t[1..];
        return t.Length > 0 ? lead + t : t;
    }

    /// <summary>A lowercase "write as" at the start of a sentence keeps the capital ("Magnetics design." stays capitalized there).</summary>
    internal static string KeepSentenceCap(string t, Match m, string to)
    {
        if (to.Length == 0 || !char.IsLower(to[0]) || !char.IsUpper(m.Value[0])) return to;
        int j = m.Index - 1;
        while (j >= 0 && (t[j] == ' ' || t[j] == '"')) j--;
        return j < 0 || t[j] is '.' or '!' or '?' or '\n' ? char.ToUpperInvariant(to[0]) + to[1..] : to;
    }

    static readonly HashSet<string> HintStop = new(StringComparer.OrdinalIgnoreCase)
        { "the", "a", "an", "and", "of", "to", "is", "on", "in", "for", "from", "with", "at", "by", "or", "it", "its", "my", "your", "our", "use", "help", "sent", "card", "house" };

    /// <summary>
    /// Accurate mode (Qwen3) word hints: the words each dictionary entry puts in that weren't in what was heard
    /// ("Lt Spice and" -> "LTspice and" gives "LTspice"; "bomb" -> "BOM" gives "BOM"). Short, distinct, 60 at most.
    /// </summary>
    public static List<string> HintTerms(IEnumerable<DictEntry> dict)
    {
        var outp = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in dict)
        {
            if (string.IsNullOrWhiteSpace(d.To)) continue;
            var from = new HashSet<string>((d.From ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
            var run = new List<string>();
            void Flush()
            {
                var term = string.Join(" ", run).Trim(',', '.', ';', ':');
                run.Clear();
                // bare numbers ("14" from "November 14") are left out: as hints they pushed Qwen3 to spell out the year
                if (term.Length >= 2 && !HintStop.Contains(term) && term.Any(char.IsLetter) && seen.Add(term)) outp.Add(term);
            }
            foreach (var w in d.To.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (from.Contains(w) || HintStop.Contains(w.Trim(',', '.'))) Flush(); else run.Add(w);
            }
            Flush();
            if (outp.Count >= 60) break;
        }
        return outp;
    }

    /// <summary>Whisper "initial prompt": vocabulary the model should expect (names, jargon, product terms).</summary>
    public static string BuildPrompt(IEnumerable<DictEntry> dict)
    {
        var terms = dict.Select(d => d.To).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Take(60);
        var list = string.Join(", ", terms);
        return list.Length == 0 ? "" : "Vocabulary: " + list + ".";
    }
}
