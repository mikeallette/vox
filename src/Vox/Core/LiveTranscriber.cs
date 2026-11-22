namespace Vox.Core;

/// <summary>
/// "Transcribe while speaking": as you talk, finished phrases (split at natural pauses) are sent to the speech engine in
/// the background, so when you let go of the key only the last phrase is left to process. Returns null from
/// <see cref="FinishAsync"/> when it detected no speech, so the caller can fall back to transcribing the whole clip.
/// </summary>
internal sealed class LiveTranscriber
{
    readonly ISpeechEngine _engine;
    readonly string _language, _prompt;
    readonly int _threads;
    readonly Segmenter _seg;
    readonly List<(float[] audio, Task<string> text)> _pieces = new();
    readonly object _gate = new();
    readonly CancellationTokenSource _cts = new();
    volatile bool _finishing;

    public int SegmentCount { get { lock (_gate) return _pieces.Count; } }
    /// <summary>True when the last piece was transcribed together with the one before it (for the log and tests).</summary>
    public bool TailMerged { get; private set; }
    const double MinChunkSec = 6;   // was 12; 6 s meets the 1.5 s wait target with ~0.3 points accuracy cost (QA 2026-11-09)

    // A short last piece heard on its own has no context: on the 2026-11-11 reading test "by the end of the day"
    // came out "Bye. End of the day" and "for your review" came out "Or you're already", both right when decoded with
    // the speech before them. So a last piece under TailMergeSec is transcribed together with the previous piece.
    // Measured 2026-11-11 (--live-replay, 25 recordings): 93.6% -> 93.9% words right, median wait 837 -> 1197 ms,
    // worst 1.7 -> 2.7 s. Not worth it, so it is off; VOX_TAILMERGE=1 turns it on, VOX_TAILMERGE_SEC sets the length.
    static readonly double TailMergeSec = Env("VOX_TAILMERGE_SEC") ?? 3.5;
    static readonly bool TailMergeOn = Environment.GetEnvironmentVariable("VOX_TAILMERGE") == "1";   // off: +0.3 points for +0.36 s median wait on the 25-recording replay (2026-11-11)
    const double MaxMergedSec = 20;     // keep the wait bounded: never re-decode more than this at release

    static double? Env(string name) => double.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float,
                                                       System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

    readonly bool _tailMerge;

    public LiveTranscriber(ISpeechEngine engine, string language, int threads, string prompt, double? minChunkSec = null, bool? tailMerge = null)
    {
        _engine = engine; _language = language; _threads = threads; _prompt = prompt; _tailMerge = tailMerge ?? TailMergeOn;
        // Pieces of at least MinChunkSec, cut at a pause, so the model hears whole sentences (context fixes words like
        // "wedding" vs "weapon"). A dictation shorter than that is transcribed in one piece at release. The segmenter
        // keeps every sample, so quiet words are never dropped. VOX_LIVE_MINCHUNK overrides it for testing.
        double minChunk = Env("VOX_LIVE_MINCHUNK") ?? minChunkSec ?? MinChunkSec;
        _seg = new Segmenter("You", 0, OnSegment, pauseFrames: 25, minChunkSec: minChunk, longPauseFrames: 1000, minSpeechFrames: 4, keepAll: true);
    }

    Task<string> Transcribe(float[] samples)
    {
        var ct = _cts.Token;
        // Pad the end with a little silence (some engines clip the last syllable of a segment). The speech gate then
        // skips phrases with nobody talking, trims, levels and transcribes (off the audio thread).
        var padded = new float[samples.Length + 3200];
        Array.Copy(samples, padded, samples.Length);
        return Task.Run(() => SpeechGate.TranscribeAsync(_engine, padded, _language, _threads, _prompt, condition: true, ct), ct);
    }

    void OnSegment(AudioSegment s)
    {
        lock (_gate)
        {
            if (_finishing && _tailMerge && _pieces.Count > 0 && s.Samples.Length < TailMergeSec * 16000)
            {
                var prev = _pieces[^1].audio;
                if ((prev.Length + s.Samples.Length) / 16000.0 <= MaxMergedSec)
                {
                    var both = new float[prev.Length + s.Samples.Length];
                    prev.CopyTo(both, 0); s.Samples.CopyTo(both, prev.Length);
                    _pieces[^1] = (both, Transcribe(both));    // the earlier result for that piece is simply not used
                    TailMerged = true;
                    return;
                }
            }
            _pieces.Add((s.Samples, Transcribe(s.Samples)));
        }
    }

    /// <summary>Feed microphone samples (16 kHz mono float). Called from the audio thread.</summary>
    public void Push(float[] samples) => _seg.Push(samples);

    public async Task<string?> FinishAsync()
    {
        _finishing = true;
        _seg.Flush();                                   // the phrase still in progress becomes the last segment
        Task<string>[] all;
        lock (_gate) all = _pieces.Select(p => p.text).ToArray();
        if (all.Length == 0) return null;
        var parts = await Task.WhenAll(all);
        return string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()));
    }

    public void Cancel() { try { _cts.Cancel(); } catch { } }
}
