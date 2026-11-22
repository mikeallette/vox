using System.Text;
using System.Text.RegularExpressions;

namespace Vox.Core;

/// <summary>
/// Engineering-style numbers and units (added 2026-11-08 after the 51-round test: "volts", "amps" and "10k ohm"
/// were the most common mistakes). Only touches a number that is followed by a unit, a time of day or "percent",
/// so ordinary words like "one of the boards" stay as they are.
///   "forty eight volts" / "48 volts" / "48V"  -> "48 V"        "two point two K ohm" / "2.2k ohm" -> "2.2 kΩ"
///   "eight amps" -> "8 A"   "four hundred kilohertz" -> "400 kHz"   "eighty percent" -> "80%"
///   "nine thirty a m" / "8 10 pm" -> "9:30 a.m." / "8:10 p.m."    "25 degrees C" -> "25 degrees Celsius"
/// </summary>
internal static class NumberFormat
{
    static readonly Dictionary<string, int> Small = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9,
        ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16,
        ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19,
    };
    static readonly Dictionary<string, int> Tens = new(StringComparer.OrdinalIgnoreCase)
    {
        ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50, ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90,
    };
    const string NumWord = "zero|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen|twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety|hundred|thousand|oh";

    // unit words -> symbol. Longest spellings first so "kilohertz" wins over "hertz".
    static readonly (string pattern, string symbol)[] Units =
    {
        (@"[kK](?i:ilo)?[ -]?(?i:ohms?)|(?i:kilohms?)|[kK]", "kΩ"),          // "K" alone only before pull, resistor, load (see Apply)
        (@"(?i:meg(?:a)?[ -]?ohms?|megohms?)", "MΩ"),
        (@"(?i:milli[ -]?ohms?)", "mΩ"),
        (@"(?i:ohms?)", "Ω"),
        (@"(?i:kilovolts?)|kV|KV", "kV"),
        (@"(?i:millivolts?|milvolts?)|mV", "mV"),
        (@"(?i:volts?|vol)|V", "V"),
        (@"(?i:milliamps?|milliamperes?)|mA", "mA"),
        (@"(?i:amps?|amperes?)|A", "A"),
        (@"(?i:kilohertz)|kHz|khz|KHz", "kHz"),
        (@"(?i:megahertz)|MHz|mhz", "MHz"),
        (@"(?i:hertz)|Hz", "Hz"),
        (@"(?i:kilowatts?)|kW", "kW"),
        (@"(?i:milliwatts?)|mW", "mW"),
        (@"(?i:watts?)|W", "W"),
        (@"(?i:decibels?)|dee bee|D B|dB|db", "dB"),
        (@"(?i:micro[ -]?farads?)|µF|uF", "µF"),          // "micro farad" (Qwen3 writes it apart)
        (@"(?i:nano[ -]?farads?)|nF", "nF"),
        (@"(?i:pico[ -]?farads?)|pF", "pF"),
        (@"microhenr(?:y|ies|ys)|µH|uH", "µH"),
        (@"millimeters?|millimetres?|mm", "mm"),
    };
    static readonly Regex UnitRx;
    static readonly Dictionary<string, string> UnitOf = new();
    static readonly Regex SpokenNumber = new($@"\b(?:{NumWord})(?:(?:[\s-]+|[\s-]+and[\s-]+)(?:{NumWord}|point))*(?:[\s-]+point(?:[\s-]+(?:{NumWord}))+)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex AmPm = new(@"^[\s,]*([ap])\.? ?m\b\.?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Percent = new(@"\b(\d+(?:\.\d+)?) ?(?:percent(?:a|age)?|per cent)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex DegreesUnit = new(@"(?<![\w.])(\d+(?:\.\d+)?) ?(?:degrees?|°) ?(Celsius|centigrade|Fahrenheit|C|c|F)(?![\w])", RegexOptions.Compiled);
    static readonly Regex DegreesPlain = new(@"(?<![\w.])(\d+(?:\.\d+)?) degrees?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex GluedDegrees = new(@"(\d) (?:degre+e?s?C|deC)\b",RegexOptions.Compiled);   // "105 degreC" (the model ran "degrees C" together)
    // "by nine fifteen", "at two thirty": a time of day without a.m./p.m.
    static readonly Regex TimeLead = new(@"\b(?:at|by|until|till|around|before|after|from|since)\s+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Duration = new(@"^[\s-]+(?:seconds?|minutes?|hours?|days?|weeks?|months?|years?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Dollars = new(@"\b(\d+(?:,\d{3})*(?:\.\d\d)?) dollars?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex ClockSpaced = new(@"\b(1[0-2]|0?[1-9])[ -]([0-5]\d) ?([ap])\.? ?m\b\.?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static NumberFormat()
    {
        var alts = new List<string>();
        for (int i = 0; i < Units.Length; i++) { alts.Add($"(?<u{i}>{Units[i].pattern})"); }
        // number, optional space or hyphen, unit, then not followed by more letters (so "5 apples" is untouched)
        UnitRx = new Regex($@"(?<![\w.])(?<n>\d+(?:\.\d+)?)(?:[ -]?)(?:{string.Join("|", alts)})(?![\w])", RegexOptions.Compiled);
    }

    // "tenam", "eightpm" (the model ran the number and am/pm together)
    static readonly Regex GluedAmPm = new($@"\b({NumWord})([ap]m)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // "May tenth", "December 2nd", "July twenty-eighth" -> "May 10", "December 2", "July 28"
    const string Month = "January|February|March|April|May|June|July|August|September|October|November|December|Jan|Feb|Mar|Apr|Jun|Jul|Aug|Sept|Sep|Oct|Nov|Dec";
    static readonly Dictionary<string, int> Ordinal = new(StringComparer.OrdinalIgnoreCase)
    {
        ["first"] = 1, ["second"] = 2, ["third"] = 3, ["fourth"] = 4, ["fifth"] = 5, ["sixth"] = 6, ["seventh"] = 7, ["eighth"] = 8, ["ninth"] = 9,
        ["tenth"] = 10, ["eleventh"] = 11, ["twelfth"] = 12, ["thirteenth"] = 13, ["fourteenth"] = 14, ["fifteenth"] = 15, ["sixteenth"] = 16,
        ["seventeenth"] = 17, ["eighteenth"] = 18, ["nineteenth"] = 19, ["twentieth"] = 20, ["thirtieth"] = 30,
    };
    static readonly Regex MonthDay = new($@"\b({Month})\.? (?:(\d{{1,2}})(?:st|nd|rd|th)\b|(?:(twenty|thirty)[ -])?(first|second|third|fourth|fifth|sixth|seventh|eighth|ninth|tenth|eleventh|twelfth|thirteenth|fourteenth|fifteenth|sixteenth|seventeenth|eighteenth|nineteenth|twentieth|thirtieth)\b)", RegexOptions.Compiled);

    // "November 14, two thousand twenty-six" / "in two thousand nine" -> 2026 / 2009 (Qwen3 says years this way).
    // Only after a month and day or "in/since/by/until/from/of", so "two thousand units" stays as words.
    static readonly Regex YearWords = new($@"(?<=(?:\b(?:{Month})\.? \d{{1,2}},? |\b(?:in|since|by|until|from|of) ))two thousand(?: and)?(?:[ -]((?:twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety)(?:[ -](?:one|two|three|four|five|six|seven|eight|nine))?|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen))?\b(?![ -](?:{NumWord})\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static string YearFromWords(Match m)
    {
        if (!m.Groups[1].Success) return m.Value.StartsWith("two thousand and", StringComparison.OrdinalIgnoreCase) ? m.Value : "2000";
        int n = 0;
        foreach (var w in m.Groups[1].Value.Split(' ', '-'))
            n += Small.TryGetValue(w, out var s) ? s : Tens.TryGetValue(w, out var t) ? t : 0;
        return (2000 + n).ToString();
    }

    public static string Apply(string t)
    {
        t = GluedAmPm.Replace(t, "$1 $2");
        t = MonthDay.Replace(t, m =>
        {
            if (m.Groups[2].Success) return $"{m.Groups[1].Value} {int.Parse(m.Groups[2].Value)}";
            int d = Ordinal[m.Groups[4].Value] + (m.Groups[3].Success ? (m.Groups[3].Value.Equals("twenty", StringComparison.OrdinalIgnoreCase) ? 20 : 30) : 0);
            return d is >= 1 and <= 31 ? $"{m.Groups[1].Value} {d}" : m.Value;
        });
        t = YearWords.Replace(t, YearFromWords);   // after MonthDay, so "November fourteenth, two thousand ..." works
        t = SpokenNumbers(t);
        t = ClockSpaced.Replace(t, m => $"{m.Groups[1].Value.TrimStart('0')}:{m.Groups[2].Value} {char.ToLowerInvariant(m.Groups[3].Value[0])}.m.");
        t = UnitRx.Replace(t, m =>
        {
            for (int i = 0; i < Units.Length; i++)
            {
                var g = m.Groups["u" + i];
                if (!g.Success) continue;
                // single capital letters are only units when written that way already ("5V", "8 A"), never "a"/"v" words
                if (g.Value.Length == 1 && g.Value != "V" && g.Value != "A" && g.Value != "W" && g.Value != "K" && g.Value != "k") return m.Value;
                if (g.Value is "K" or "k" && !Regex.IsMatch(t.AsSpan(m.Index + m.Length).ToString(), @"^\s*(?:pull|resistor|load|ohm)")) return m.Value;
                return m.Groups["n"].Value + " " + Units[i].symbol;
            }
            return m.Value;
        });
        t = Percent.Replace(t, "$1%");
        t = Dollars.Replace(t, "$$$1");
        // Degrees as symbols, the way engineering notes write them (script read 2026-11-11):
        // "85 degrees Celsius" / "85 degrees c" / "105 degreC" -> "85°C", "55 degrees of phase margin" -> "55°"
        t = GluedDegrees.Replace(t, "$1°C");
        t = DegreesUnit.Replace(t, m => m.Groups[1].Value + "°" + (char.ToUpperInvariant(m.Groups[2].Value[0]) == 'F' ? "F" : "C"));
        t = DegreesPlain.Replace(t, "$1°");
        return t;
    }

    /// <summary>Spoken numbers become digits, but only before a unit, "percent", "degrees" or a time of day.</summary>
    static string SpokenNumbers(string t)
    {
        return SpokenNumber.Replace(t, m =>
        {
            var after = t[(m.Index + m.Length)..];
            bool ampm = AmPm.IsMatch(after);
            bool duration = Duration.IsMatch(after);
            bool unit = ampm || duration || Regex.IsMatch(after, @"^[\s-]+(?:percent|per cent|degrees?\b|" + UnitWordsForSpoken + ")", RegexOptions.IgnoreCase);
            if (!unit && TimeLead.IsMatch(t[..m.Index]) && Parse(m.Value) is { Count: 2 } hm
                && hm[0] is >= 1 and <= 12 && hm[0] == Math.Floor(hm[0]) && hm[1] is >= 0 and < 60 && hm[1] == Math.Floor(hm[1])
                && (hm[1] >= 10 || m.Value.Contains("oh", StringComparison.OrdinalIgnoreCase)))
                return $"{hm[0]}:{hm[1]:00}";                                    // "by nine fifteen" -> "by 9:15"
            // "one point two" -> "1.2", "eight point zero point three" -> "8.0.3", but "at one point two people left" is a phrase
            if (!unit) return Regex.IsMatch(t[..m.Index], @"\bat\s+$", RegexOptions.IgnoreCase) && m.Value.StartsWith("one", StringComparison.OrdinalIgnoreCase) ? m.Value : Dotted(m.Value) ?? m.Value;
            var groups = Parse(m.Value);
            if (groups == null || groups.Count == 0) return m.Value;
            if (ampm && groups.Count == 2 && groups[0] is >= 1 and <= 12 && groups[1] is >= 0 and < 60 && groups[1] == Math.Floor(groups[1]))
                return $"{groups[0]}:{groups[1]:00}";
            if (groups.Count != 1) return m.Value;
            if (duration && groups[0] < 10) return m.Value;                     // "five minutes" stays; "forty five minutes" -> "45 minutes"
            return groups[0].ToString(System.Globalization.CultureInfo.InvariantCulture);
        });
    }
    const string UnitWordsForSpoken = @"dollars?\b|[kK][ -]+(?:pull|resistor|load)|milli[ -]?ohms?\b|k(?:ilo)?[ -]?ohms?\b|kilohms?\b|K ohms?\b|meg(?:a)?[ -]?ohms?\b|ohms?\b|kilovolts?\b|millivolts?\b|volts?\b|milliamps?\b|amps?\b|amperes?\b|kilohertz\b|megahertz\b|hertz\b|kilowatts?\b|milliwatts?\b|watts?\b|decibels?\b|D B\b|dB\b|micro[ -]?farads?\b|nano[ -]?farads?\b|pico[ -]?farads?\b|microhenr|millimet|(?-i:kHz|MHz|Hz|kV|kW|mV|mA|µF|nF|pF)\b";   // "two-kHz" -> "2 kHz"

    /// <summary>Decimals and version numbers said with "point": every part must be a plain number or a run of digits.</summary>
    static string? Dotted(string s)
    {
        var parts = Regex.Split(s.Trim(), @"[\s-]+point[\s-]+", RegexOptions.IgnoreCase);
        if (parts.Length < 2) return null;
        var outp = new List<string>();
        for (int i = 0; i < parts.Length; i++)
        {
            var g = Parse(parts[i].Replace("oh", "zero", StringComparison.OrdinalIgnoreCase));
            if (g == null || g.Count == 0 || g.Any(x => x != Math.Floor(x))) return null;
            if (g.Count == 1) outp.Add(g[0].ToString(System.Globalization.CultureInfo.InvariantCulture));
            else if (i > 0 && g.All(x => x is >= 0 and <= 9)) outp.Add(string.Concat(g));     // "one four" after a point = "14"
            else return null;
        }
        return string.Join(".", outp);
    }

    /// <summary>
    /// "two hundred fifty" -> [250];  "two point two" -> [2.2];  "nine thirty" -> [9, 30] (two numbers side by side,
    /// as in a time). Returns null for anything it does not understand.
    /// </summary>
    static List<decimal>? Parse(string s)
    {
        var words = Regex.Split(s.Trim(), @"[\s-]+").Where(w => w.Length > 0 && !w.Equals("and", StringComparison.OrdinalIgnoreCase)).ToList();
        var groups = new List<decimal>();
        long total = 0, cur = 0; bool any = false; string last = "";   // last: "unit", "teen", "tens", "hundred", "thousand"
        int i = 0;
        void Flush() { if (any) groups.Add(total + cur); total = 0; cur = 0; any = false; last = ""; }
        for (; i < words.Count; i++)
        {
            var w = words[i].ToLowerInvariant();
            if (w == "point")
            {
                if (!any) return null;
                var sb = new StringBuilder();
                for (i++; i < words.Count; i++)
                {
                    var d = words[i].ToLowerInvariant();
                    if (d == "oh") d = "zero";
                    if (!Small.TryGetValue(d, out var v) || v > 9) return null;
                    sb.Append(v);
                }
                if (sb.Length == 0) return null;
                groups.Add(total + cur + decimal.Parse("0." + sb, System.Globalization.CultureInfo.InvariantCulture));
                any = false; total = cur = 0;
                break;
            }
            if (w == "oh") return null;
            if (Small.TryGetValue(w, out var sv))
            {
                if (any && (last == "unit" || last == "teen" || (last == "tens" && sv >= 10))) Flush();
                cur += sv; any = true; last = sv >= 10 ? "teen" : "unit"; continue;
            }
            if (Tens.TryGetValue(w, out var tv))
            {
                if (any && (last == "unit" || last == "teen" || last == "tens")) Flush();
                cur += tv; any = true; last = "tens"; continue;
            }
            if (w == "hundred") { if (last == "hundred") return null; cur = (cur == 0 ? 1 : cur) * 100; any = true; last = "hundred"; continue; }
            if (w == "thousand") { total += (cur == 0 ? 1 : cur) * 1000; cur = 0; any = true; last = "thousand"; continue; }
            return null;
        }
        if (any) groups.Add(total + cur);
        return groups;
    }
}
