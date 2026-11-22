using System.Text.RegularExpressions;
using Whisper.net;

namespace Vox.Core;

/// <summary>
/// Speech gate (added 2026-11-08, step 1 of the accuracy research). Parakeet v3 guesses among 25 languages and
/// cannot be locked to English, so noise with nobody talking can come out as a made-up word ("Actualmente.").
/// The gate keeps noise away from the model:
///   1. Silero VAD (ggml-silero-*.bin next to the speech model, run by whisper.cpp inside Whisper.net, CPU, offline)
///      finds the speech. Under about a quarter second of speech means nobody talked: nothing is transcribed.
///   2. Leading and trailing non-speech is trimmed (with generous padding, so soft first and last sounds survive).
///   3. A very short result from very little speech is dropped when it looks made up: a foreign word, a known
///      noise phrase, or low model confidence.
/// If the VAD model is missing, every check passes through and Vox works exactly as before.
/// VOX_GATE=0 turns it off (used by the A/B test).
/// </summary>
internal static class SpeechGate
{
    public const string ModelFile = "ggml-silero-v5.1.2.bin";
    public const double MinSpeechSec = 0.25;
    public static double PadSec => double.TryParse(Environment.GetEnvironmentVariable("VOX_PAD"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : DefaultPad;
    const double DefaultPad = 0.35;
    const float Threshold = 0.35f;                       // below Silero's 0.5 default: laptop mics record quietly; keep soft words

    static readonly object Lock = new();
    static WhisperVadFactory? _factory;
    static WhisperVadProcessor? _vad;
    static string _tried = "";

    public static bool Available => _vad != null && Environment.GetEnvironmentVariable("VOX_GATE") != "0";

    public readonly record struct Speech(bool Known, double SpeechSec, double FirstSec, double LastSec, int Segments);

    /// <summary>Looks for the VAD model in the app's engine folder (where the build puts it), then next to the speech model.</summary>
    public static void Init(string speechModelPath)
    {
        lock (Lock)
        {
            if (_vad != null) return;
            var dirs = new[] { ParakeetEngine.EngineDir, Path.GetDirectoryName(speechModelPath) ?? "", Path.Combine(AppContext.BaseDirectory, "models") };
            var path = dirs.Select(d => Path.Combine(d, ModelFile)).FirstOrDefault(File.Exists);
            if (path == null || path == _tried) return;
            _tried = path;
            try
            {
                _factory = WhisperVadFactory.FromPath(path);
                _vad = _factory.CreateBuilder()
                    .WithThreads(1)
                    .WithThreshold(Threshold)
                    .WithMinSpeechDuration(TimeSpan.FromMilliseconds(120))
                    .WithMinSilenceDuration(TimeSpan.FromMilliseconds(300))
                    .WithSpeechPadding(TimeSpan.FromMilliseconds(60))
                    .Build();
                Log.Info("speech gate: Silero VAD loaded from " + path);
            }
            catch (Exception ex) { Log.Error("speech gate: could not load " + path, ex); _vad = null; }
        }
    }

    /// <summary>Where the speech is, in seconds. Known=false means the gate is off or failed: treat as "maybe speech".</summary>
    public static Speech Analyze(float[] samples)
    {
        if (!Available || samples.Length < 1600) return new(false, 0, 0, 0, 0);
        try
        {
            // judge the levelled audio (laptop mics often record quietly), but never amplify near-silence into "speech"
            var x = AudioPrep.Condition(samples);
            double total = 0, first = double.MaxValue, last = 0; int n = 0;
            lock (Lock)
            {
                _vad!.ResetState();
                foreach (var s in _vad.DetectSpeech(x))
                {
                    double a = s.Start.TotalSeconds, b = s.End.TotalSeconds;
                    if (b <= a) continue;
                    total += b - a; first = Math.Min(first, a); last = Math.Max(last, b); n++;
                }
            }
            return n == 0 ? new(true, 0, 0, 0, 0) : new(true, total, first, last, n);
        }
        catch (Exception ex) { Log.Error("speech gate: VAD failed", ex); return new(false, 0, 0, 0, 0); }
    }

    public static bool NoSpeech(Speech s) => s.Known && s.SpeechSec < MinSpeechSec;

    /// <summary>Cuts leading and trailing non-speech, keeping PadSec on each side.</summary>
    public static float[] Trim(float[] samples, Speech s)
    {
        if (!s.Known || s.Segments == 0 || Environment.GetEnvironmentVariable("VOX_TRIM") == "0") return samples;
        const int Rate = 16000;
        int a = Math.Max(0, (int)((s.FirstSec - PadSec) * Rate)), b = Math.Min(samples.Length, (int)((s.LastSec + PadSec) * Rate));
        if (b - a < Rate / 4 || (a == 0 && b == samples.Length)) return samples;
        return samples[a..b];
    }

    // Words the model produces from noise. Short results only, so normal sentences are never touched.
    static readonly HashSet<string> NoisePhrases = new(StringComparer.OrdinalIgnoreCase)
    {
        "actualmente", "gracias", "bueno", "vale", "sí", "si", "merci", "oui", "danke", "ja", "nein", "grazie", "obrigado",
        "thanks for watching", "mm", "hmm", "mhm",       // not "thank you" or "bye": people really dictate those
    };
    static readonly Regex Foreign = new(@"[À-ÖØ-öø-ÿĀ-žА-я]", RegexOptions.Compiled);

    /// <summary>
    /// The gated way to transcribe one piece of audio: skip it if nobody talked, trim the ends, level it,
    /// transcribe, and drop a short made-up result. Used by dictation, live phrases and the test harness alike.
    /// </summary>
    public static async Task<string> TranscribeAsync(ISpeechEngine engine, float[] samples, string language, int threads, string prompt,
                                                     bool condition, CancellationToken ct = default)
    {
        // under 0.1 s there is nothing to hear; the engine was seen returning the previous result for empty audio (QA T5)
        if (samples.Length < 1600) return "";
        var s = Analyze(samples);
        if (NoSpeech(s)) { Log.Info($"speech gate: no speech in {samples.Length / 16000.0:0.0}s of audio, skipped"); return ""; }
        var audio = engine is SpeechEngine { WantsWholeClip: true } ? samples : Trim(samples, s);
        if (condition) audio = AudioPrep.Condition(audio);
        var parts = new List<string>();
        float conf = -1;
        foreach (var piece in SplitLong(audio))
        {
            string t; float c = -1;
            if (engine is SpeechEngine se) (t, c) = await se.TranscribeDetailedAsync(piece, language, threads, prompt, ct);
            else t = await engine.TranscribeAsync(piece, language, threads, prompt, ct);
            if (!string.IsNullOrWhiteSpace(t)) parts.Add(t.Trim());
            if (c >= 0) conf = conf < 0 ? c : Math.Min(conf, c);
        }
        var text = string.Join(" ", parts);
        if (LooksMadeUp(text, s, conf)) { Log.Info($"speech gate: dropped \"{text}\" (speech {s.SpeechSec:0.00}s, confidence {conf:0.00})"); return ""; }
        return text;
    }

    /// <summary>
    /// Parakeet drops words on one long piece of audio (QA T5, 2026-11-09: 5 minutes in one go typed 461-585 of about
    /// 700 words and used 3.3 GB). Anything over 35 s is cut into 20-30 s pieces at the quietest moment in that window.
    /// </summary>
    internal static List<float[]> SplitLong(float[] x)
    {
        const int R = 16000, Win = R / 5, Hop = R / 50;
        var pieces = new List<float[]>();
        int pos = 0;
        while (x.Length - pos > 35 * R)
        {
            int best = pos + 25 * R; double bestE = double.MaxValue;
            for (int i = pos + 20 * R; i + Win <= pos + 30 * R; i += Hop)
            {
                double e = 0;
                for (int k = i; k < i + Win; k += 4) e += x[k] * x[k];
                if (e < bestE) { bestE = e; best = i + Win / 2; }
            }
            pieces.Add(x[pos..best]);
            pos = best;
        }
        pieces.Add(pos == 0 ? x : x[pos..]);
        return pieces;
    }

    /// <summary>True when a short result from very little speech is probably made up from noise.</summary>
    public static bool LooksMadeUp(string text, Speech s, float confidence)
    {
        if (!Available) return false;
        var t = (text ?? "").Trim();
        if (t.Length == 0) return false;
        // a lone "Mm-." / "Hmm." is never worth typing, however long the noise that produced it (QA gate test)
        if (Regex.IsMatch(t, @"^(?:m+|h+m+|m+h+m+)[\s.\-!?]*$", RegexOptions.IgnoreCase)) return true;
        var words = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 3) return false;
        bool little = !s.Known || s.SpeechSec < 1.2;
        if (!little) return false;
        var key = Regex.Replace(t, @"[^\p{L}\p{N}' ]", "").Trim();
        if (NoisePhrases.Contains(key)) return true;
        if (Foreign.IsMatch(t) && words.Length <= 2) return true;
        if (confidence >= 0 && confidence < 0.45f && words.Length <= 2) return true;
        return false;
    }
}
