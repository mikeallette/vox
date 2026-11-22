using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Vox.Core;

/// <summary>
/// Spoken actions on a whole dictation (with "Voice commands" on):
///   "scratch that"                      -> remove the last dictation Vox typed (when that is safe)
///   "... press enter" / "... hit enter" -> type the text, then press Enter
///   "note to self, ..."                 -> save to Notes.md in the data folder instead of typing
///   "calculate 400 over 650 as a percent" -> types 61.54%
/// "Send it" is not a command: "I'll send it" is ordinary speech.
/// </summary>
internal static class VoiceActions
{
    static readonly Regex Scratch = new(@"^\W*(?:scratch that|delete that|undo that)\W*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Enter = new(@"(?:^|[\s,.;:!?]+)(?:press|hit) (?:the )?enter(?: key)?[.!]?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Note = new(@"^\W*(?:note to self|note to myself)\b[,.:;!]?\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // only "calculate"/"compute": "what is" starts too many ordinary sentences
    static readonly Regex Calc = new(@"^\W*(?:calculate|compute)\b[,:]?\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsScratchThat(string text) => Scratch.IsMatch(text);

    /// <summary>Removes a trailing "press enter"; true when it was there. rest may be empty (Enter alone).</summary>
    public static bool SplitPressEnter(string text, out string rest)
    {
        var m = Enter.Match(text);
        if (!m.Success) { rest = text; return false; }
        rest = text[..m.Index].TrimEnd(' ', ',', ';', ':');
        return true;
    }

    public static bool TryNoteToSelf(string text, out string note)
    {
        var m = Note.Match(text);
        note = m.Success ? text[m.Length..].Trim() : "";
        if (note.Length > 0) note = char.ToUpperInvariant(note[0]) + note[1..];
        return m.Success && note.Length > 0;
    }

    /// <summary>Appends a dated line to &lt;data&gt;\Notes.md and returns the file path.</summary>
    public static string SaveNote(string dataRoot, string note)
    {
        var path = Path.Combine(dataRoot, "Notes.md");
        bool isNew = !File.Exists(path);
        var line = $"- **{DateTime.Now:ddd MMM d, yyyy h:mm tt}** {note.Replace("\r\n", " ").Replace('\n', ' ')}\n";
        File.AppendAllText(path, (isNew ? "# Notes to self\n\n" : "") + line, new UTF8Encoding(false));
        return path;
    }

    // ---------------- calculator ----------------

    /// <summary>"calculate ..." with an expression it can work out: result text such as "61.54%" or "1.25".</summary>
    public static bool TryCalculate(string text, out string result)
    {
        result = "";
        var m = Calc.Match(text);
        if (!m.Success) return false;
        var body = text[m.Length..];
        try
        {
            var tokens = Tokenize(body, out bool asPercent);
            if (tokens.Count == 0 || !tokens.Any(t => t.Num.HasValue)) return false;
            int pos = 0;
            double v = Expr(tokens, ref pos);
            if (pos != tokens.Count || double.IsNaN(v) || double.IsInfinity(v)) return false;
            result = asPercent ? Format(v * 100) + "%" : Format(v);
            return true;
        }
        catch { return false; }
    }

    static string Format(double v)
    {
        if (v == 0) return "0";
        double mag = Math.Floor(Math.Log10(Math.Abs(v)));
        int decimals = (int)Math.Clamp(3 - mag, 0, 6);          // 4 significant figures
        return Math.Round(v, decimals).ToString("0.######", CultureInfo.InvariantCulture);
    }

    record Tok(double? Num, string Op);

    static readonly Dictionary<string, int> Small = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"] = 0, ["oh"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9,
        ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16,
        ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19, ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50,
        ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90,
    };
    static readonly HashSet<string> UnitWords = new(StringComparer.OrdinalIgnoreCase)
    { "v", "volts", "volt", "a", "amps", "amp", "w", "watts", "watt", "ω", "ohms", "ohm", "kω", "mv", "ma", "mw", "kv", "kw", "hz", "khz", "mhz", "s", "ms", "µs", "ns", "f", "µf", "nf", "pf", "h", "µh", "°c", "degrees", "minutes", "hours", "seconds", "dollars", "$" };

    static List<Tok> Tokenize(string s, out bool asPercent)
    {
        asPercent = false;
        s = s.ToLowerInvariant().Replace("×", " times ").Replace("÷", " divided by ").Replace("*", " times ").Replace("+", " plus ")
             .Replace("/", " over ").Replace("^", " to the power of ").Replace(",", " ").Replace("?", " ").Replace("(", " ").Replace(")", " ");
        s = Regex.Replace(s, @"(?<=\d)%", " percent");
        s = Regex.Replace(s, @"\.(?!\d)", " ");
        // "as a percent" / "in percent" / "as a percentage" at the end
        // ("calculate 15 percent" is just 0.15: a bare trailing "percent" only means "answer as a percent" after a sum)
        var pm = Regex.Match(s, @"\b(?:(as an? |in |into )percent(?:age)?|percent(?:age)?)\s*$");
        bool hasOperator = Regex.IsMatch(pm.Success ? s[..pm.Index] : s, @"\b(?:plus|minus|times|x|over|divided|multiplied|squared|cubed|power|root)\b");
        if (pm.Success && (pm.Groups[1].Success || hasOperator) && !Regex.IsMatch(s[..pm.Index], @"percent\s*$")) { asPercent = true; s = s[..pm.Index]; }
        s = Regex.Replace(s, @"\s+", " ").Trim();

        var phrases = new (string p, string op)[]
        {
            ("multiplied by", "*"), ("divided by", "/"), ("to the power of", "^"), ("square root of", "sqrt"), ("percent of", "%of"),
            ("plus", "+"), ("minus", "-"), ("times", "*"), ("x", "*"), ("over", "/"), ("squared", "sq"), ("cubed", "cube"), ("percent", "%"),
            ("and", "&"), ("point", "."), ("negative", "neg"), ("equals", "="), ("is", "="), ("the", ""), ("of", "of"),
        };
        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var outp = new List<Tok>();
        for (int i = 0; i < words.Count;)
        {
            var w = words[i];
            if (double.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) { outp.Add(new(d, "")); i++; continue; }
            if (Small.ContainsKey(w) || w is "hundred" or "thousand" or "million")
            {
                double total = 0, cur = 0; int j = i;
                while (j < words.Count)
                {
                    var x = words[j];
                    if (Small.TryGetValue(x, out var v)) cur += v;
                    else if (x == "hundred") cur = Math.Max(1, cur) * 100;
                    else if (x == "thousand") { total += Math.Max(1, cur) * 1000; cur = 0; }
                    else if (x == "million") { total += Math.Max(1, cur) * 1_000_000; cur = 0; }
                    else if (x == "and" && j + 1 < words.Count && (Small.ContainsKey(words[j + 1]))) { }
                    else break;
                    j++;
                }
                double num = total + cur;
                if (j + 1 < words.Count && words[j] == "point")
                {
                    var frac = new StringBuilder(); j++;
                    while (j < words.Count && Small.TryGetValue(words[j], out var dg) && dg < 10) { frac.Append(dg); j++; }
                    if (frac.Length > 0) num += double.Parse("0." + frac, CultureInfo.InvariantCulture);
                }
                outp.Add(new(num, "")); i = j; continue;
            }
            bool matched = false;
            foreach (var (p, op) in phrases)
            {
                var pw = p.Split(' ');
                if (i + pw.Length <= words.Count && pw.Select((x, k) => words[i + k] == x).All(b => b))
                {
                    if (op is not ("" or "=" or "&")) outp.Add(new(null, op == "of" ? "*" : op));
                    i += pw.Length; matched = true; break;
                }
            }
            if (matched) continue;
            if (UnitWords.Contains(w)) { i++; continue; }      // "400 V over 650 V": units don't change the ratio
            throw new FormatException(w);
        }
        // "percent of" -> times 1/100; a lone "%" after a number -> /100
        var fixedUp = new List<Tok>();
        foreach (var t in outp)
        {
            if (t.Op == "%of") { fixedUp.Add(new(null, "*")); fixedUp.Add(new(0.01, "")); fixedUp.Add(new(null, "*")); }
            else if (t.Op == "%") { fixedUp.Add(new(null, "*")); fixedUp.Add(new(0.01, "")); }
            else fixedUp.Add(t);
        }
        return fixedUp;
    }

    static double Expr(List<Tok> t, ref int p)
    {
        double v = Term(t, ref p);
        while (p < t.Count && t[p].Op is "+" or "-") { var op = t[p++].Op; var r = Term(t, ref p); v = op == "+" ? v + r : v - r; }
        return v;
    }
    static double Term(List<Tok> t, ref int p)
    {
        double v = Power(t, ref p);
        while (p < t.Count && t[p].Op is "*" or "/") { var op = t[p++].Op; var r = Power(t, ref p); v = op == "*" ? v * r : v / r; }
        return v;
    }
    static double Power(List<Tok> t, ref int p)
    {
        double v = Unary(t, ref p);
        while (p < t.Count)
        {
            if (t[p].Op == "sq") { v *= v; p++; }
            else if (t[p].Op == "cube") { v = v * v * v; p++; }
            else if (t[p].Op == "^") { p++; v = Math.Pow(v, Unary(t, ref p)); }
            else break;
        }
        return v;
    }
    static double Unary(List<Tok> t, ref int p)
    {
        if (p >= t.Count) throw new FormatException("end");
        if (t[p].Op is "-" or "neg") { p++; return -Unary(t, ref p); }
        if (t[p].Op == "sqrt") { p++; return Math.Sqrt(Unary(t, ref p)); }
        if (t[p].Num is double d) { p++; return d; }
        throw new FormatException(t[p].Op);
    }
}
