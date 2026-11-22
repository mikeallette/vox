using System.Text.RegularExpressions;

namespace Vox.Core;

/// <summary>
/// Electronics shorthand, applied with "Write numbers and units like an engineer":
///   reference designators  "R twelve" / "R 12" -> R12, "Q three" -> Q3, "TP 4" -> TP4
///   spelled part prefixes  "T P S five four three six zero" -> TPS54360
///   part numbers           "TPS 54360" -> TPS54360, "LM three one seven" -> LM317
/// Standards ("ISO 9001", "IPC 2221") and years ("CES 2026") are left alone, and only capital letters count, so
/// "a 5" or "I 2" never change.
/// </summary>
internal static class PartNumbers
{
    const string Digit = "zero|oh|one|two|three|four|five|six|seven|eight|nine";
    static readonly Dictionary<string, int> DigitVal = new(StringComparer.OrdinalIgnoreCase)
    { ["zero"] = 0, ["oh"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9 };
    static readonly Dictionary<string, int> Teen = new(StringComparer.OrdinalIgnoreCase)
    { ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19 };
    static readonly Dictionary<string, int> Tens = new(StringComparer.OrdinalIgnoreCase)
    { ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50, ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90 };

    static readonly HashSet<string> Standards = new(StringComparer.Ordinal)
    { "ISO", "IEC", "IEEE", "RFC", "UL", "MIL", "ASTM", "IPC", "ANSI", "NEMA", "AWG", "EN", "CISPR", "DO", "JEDEC", "USB", "PCIE", "DDR", "COVID", "CES", "AM", "PM" };

    // "T P S" before a number -> "TPS" (two or more capital letters said one by one, then digits)
    static readonly Regex SpelledPrefix = new($@"\b((?:[A-Z] ){{1,4}}[A-Z])(?= (?:\d|(?:{Digit})\b))", RegexOptions.Compiled);

    // a capital prefix followed by digit words said one at a time: "LM three one seven" -> "LM 317"
    static readonly Regex PrefixDigitWords = new($@"\b([A-Z]{{2,5}}) ((?:(?:{Digit})[ -]){{1,7}}(?:{Digit}))\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // A value followed by a unit is a quantity ("LLC 500 kHz", "C 5 microfarad"), not a part number or designator. This
    // runs before NumberFormat, so units may still be spelled out.
    const string UnitAfter = @"\s?(?:%|°|[kmµMnpuG]?(?:V|A|W|Ω|F|H|Hz|s|m|g)\b|(?i:(?:kilo|mega|giga|milli|micro|nano|pico)?[ -]?(?:volts?|amps?|amperes?|ohms?|watts?|hertz|farads?|henr(?:y|ies)|seconds?|meters?|metres?|grams?)\b|percent\b|degrees?\b|mm\b|mils?\b|inch(?:es)?\b|feet\b|foot\b|minutes?\b|hours?\b|days?\b|units?\b|pieces?\b|parts?\b|times\b))";

    // "TPS 54360", "LM 317", "ADS 1115" -> joined; 3 to 6 digits, optional letter suffix ("LM 317T")
    static readonly Regex PrefixNumber = new($@"\b([A-Z]{{2,5}}) (\d{{3,6}}[A-Z]{{0,3}})\b(?![.,]\d|{UnitAfter})", RegexOptions.Compiled);

    // reference designators: one capital letter (or TP/SW/FB/CR) plus 1 to 3 digits or a number word
    const string Desig = "R|C|L|Q|U|D|J|K|F|T|Y|TP|SW|FB|CR";
    static readonly Regex DesigNumber = new($@"(?<![\w.-])({Desig})[ -](\d{{1,3}})\b(?![.,:]\d|{UnitAfter})", RegexOptions.Compiled);
    static readonly Regex DesigWords = new($@"(?<![\w.-])({Desig}) ((?:twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety)(?:[ -](?:one|two|three|four|five|six|seven|eight|nine))?|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen|one|two|three|four|five|six|seven|eight|nine)\b(?![ -](?:hundred|thousand|point)\b|{UnitAfter.Replace(@"\s?", @"[ -]")})", RegexOptions.Compiled);

    public static string Apply(string t)
    {
        t = SpelledPrefix.Replace(t, m => m.Groups[1].Value.Replace(" ", ""));
        t = PrefixDigitWords.Replace(t, m =>
        {
            var p = m.Groups[1].Value;
            if (!p.All(char.IsUpper) || Standards.Contains(p)) return m.Value;
            var digits = string.Concat(Regex.Split(m.Groups[2].Value, "[ -]").Select(w => DigitVal[w]));
            return p + " " + digits;
        });
        t = PrefixNumber.Replace(t, m =>
        {
            var p = m.Groups[1].Value; var n = m.Groups[2].Value;
            if (Standards.Contains(p)) return m.Value;
            if (n.Length == 4 && int.TryParse(n, out var y) && y is >= 1900 and <= 2099) return m.Value;   // "Q 2026"
            return p + n;
        });
        t = DesigWords.Replace(t, m => m.Groups[1].Value + WordsToInt(m.Groups[2].Value));
        t = DesigNumber.Replace(t, m => m.Groups[1].Value + m.Groups[2].Value);
        return t;
    }

    static int WordsToInt(string w)
    {
        int n = 0;
        foreach (var part in w.Split(' ', '-'))
            n += DigitVal.TryGetValue(part, out var d) ? d : Teen.TryGetValue(part, out var t) ? t : Tens.TryGetValue(part, out var x) ? x : 0;
        return n;
    }
}
