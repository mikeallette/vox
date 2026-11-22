using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Vox.Core;

/// <param name="Source">meetings: 0 = microphone, 1.. = playback device; dictation: 0</param>
public sealed record AudioSegment(string Speaker, double StartSec, float[] Samples, int Source = 0);

/// <summary>Splits a continuous 16 kHz stream into speech segments at natural pauses (simple adaptive energy VAD).</summary>
internal sealed class Segmenter
{
    // How it works (rewritten 2026-11-07 after quiet-mic dictation lost whole phrases):
    //  - Every sample from the first speech frame on is kept. Nothing inside a segment is ever dropped, so quiet words
    //    between louder ones survive. Only silence before speech starts is trimmed (to a short pre-roll).
    //  - "Speech" is relative to the measured background noise (a slow minimum tracker), not a fixed level, so a quiet
    //    microphone works as well as a loud one.
    //  - A segment ends at a pause of PauseFrames once it is at least MinChunkFrames long (longer pieces give the model
    //    more context), at any pause of LongPauseFrames, or at MaxFrames (cut at the quietest point near the end).
    const int Frame = 320;                 // 20 ms
    const int MaxFrames = 1250;            // 25 s cap (Whisper works on 30 s windows)
    const int PreRollFrames = 15;          // 0.3 s kept before the first speech frame (soft word onsets)
    const float AbsFloor = 0.0012f;        // below this RMS nothing counts as speech
    readonly int PauseFrames, LongPauseFrames, MinChunkFrames, MinSpeechFrames;
    readonly bool KeepAll;                 // dictation: send every piece (the key was held on purpose); meetings: skip silence

    readonly string _speaker;
    readonly Action<AudioSegment> _emit;
    readonly double _offsetSec;
    readonly int _source;
    readonly float[] _frame = new float[Frame];
    readonly List<float> _cur = new();     // samples of the segment being built
    readonly List<float> _rms = new();     // per-frame RMS of _cur
    int _fi, _silentRun, _speechFrames;
    long _frameIndex, _curStartFrame;
    float _noise = 0.002f;

    /// <param name="pauseFrames">pause (20 ms frames) that may end a segment once it is minChunkSec long</param>
    /// <param name="minChunkSec">shortest segment that a normal pause may end</param>
    /// <param name="longPauseFrames">pause that always ends a segment</param>
    /// <param name="minSpeechFrames">segments with fewer speech frames than this are treated as noise and skipped</param>
    public Segmenter(string speaker, double offsetSec, Action<AudioSegment> emit,
                     int pauseFrames = 40, double minChunkSec = 0, int longPauseFrames = 40, int minSpeechFrames = 12, bool keepAll = false, int source = 0)
    {
        _source = source;
        _speaker = speaker; _offsetSec = offsetSec; _emit = emit; KeepAll = keepAll;
        PauseFrames = pauseFrames; LongPauseFrames = Math.Max(pauseFrames, longPauseFrames);
        MinChunkFrames = (int)(minChunkSec * 50); MinSpeechFrames = minSpeechFrames;
    }

    public void Push(ReadOnlySpan<float> samples)
    {
        foreach (var s in samples)
        {
            _frame[_fi++] = s;
            if (_fi == Frame) { _fi = 0; Process(); }
        }
    }

    void Process()
    {
        double sum = 0; foreach (var v in _frame) sum += v * v;
        float rms = (float)Math.Sqrt(sum / Frame);
        _frameIndex++;

        // background noise: follows quiet frames quickly, rises only slowly (about 2.5% per second) during speech
        _noise = rms < _noise ? 0.8f * _noise + 0.2f * rms : Math.Min(rms, _noise * 1.0005f);
        _noise = Math.Max(_noise, 0.0002f);
        bool speech = rms > Math.Max(AbsFloor, _noise * 3f);

        if (_rms.Count == 0) _curStartFrame = _frameIndex - 1;
        _cur.AddRange(_frame); _rms.Add(rms);
        if (speech) { _speechFrames++; _silentRun = 0; } else _silentRun++;

        if (_speechFrames == 0 && !KeepAll)
        {
            // not in speech yet: keep only a short pre-roll
            if (_rms.Count > PreRollFrames) DropFront(_rms.Count - PreRollFrames);
            return;
        }

        int frames = _rms.Count;
        if (_speechFrames == 0) { if (frames >= MaxFrames) Cut(frames); return; }   // KeepAll, nothing heard yet
        if (_silentRun >= LongPauseFrames || (_silentRun >= PauseFrames && frames >= MinChunkFrames))
        {
            // cut in the middle of the pause; the second half becomes the next segment's lead-in
            Cut(frames - _silentRun / 2);
        }
        else if (frames >= MaxFrames)
        {
            int from = Math.Max(1, frames - 150), best = frames;   // quietest frame in the last 3 s
            float bestRms = float.MaxValue;
            for (int i = from; i < frames; i++) if (_rms[i] < bestRms) { bestRms = _rms[i]; best = i; }
            Cut(best);
        }
    }

    /// <summary>Emits the first <paramref name="frames"/> frames as a segment and keeps the rest.</summary>
    void Cut(int frames)
    {
        frames = Math.Clamp(frames, 1, _rms.Count);
        if (KeepAll || _speechFrames >= MinSpeechFrames)
            _emit(new AudioSegment(_speaker, _offsetSec + _curStartFrame * Frame / 16000.0, _cur.GetRange(0, frames * Frame).ToArray(), _source));
        DropFront(frames);
        // recount what is left (normally the quiet half of a pause; after a 25 s forced cut it can hold speech)
        float th = Math.Max(AbsFloor, _noise * 3f);
        _speechFrames = 0; _silentRun = 0;
        foreach (var r in _rms) { if (r > th) { _speechFrames++; _silentRun = 0; } else _silentRun++; }
    }

    void DropFront(int frames)
    {
        _cur.RemoveRange(0, frames * Frame); _rms.RemoveRange(0, frames);
        _curStartFrame += frames;
    }

    /// <summary>Emits whatever is buffered (end of recording) and resets.</summary>
    public void Flush()
    {
        if (KeepAll ? _rms.Count >= 10 : _speechFrames >= MinSpeechFrames && _rms.Count > 0)
            _emit(new AudioSegment(_speaker, _offsetSec + _curStartFrame * Frame / 16000.0, _cur.ToArray(), _source));
        _cur.Clear(); _rms.Clear(); _speechFrames = 0; _silentRun = 0;
    }
}

/// <summary>Streaming mono resampler (box low-pass + linear interpolation) to 16 kHz.</summary>
internal sealed class Resampler16k
{
    readonly double _step;
    readonly int _box;
    double _pos;
    float _last;
    readonly Queue<float> _win = new();
    double _winSum;

    public Resampler16k(int srcRate) { _step = srcRate / 16000.0; _box = Math.Max(1, (int)Math.Round(_step)); }

    public void Process(float[] mono, int n, List<float> output)
    {
        if (n == 0) return;
        // low-pass (moving average) to limit aliasing before decimation
        var f = mono;
        if (_box > 1)
        {
            f = new float[n];
            for (int i = 0; i < n; i++)
            {
                _win.Enqueue(mono[i]); _winSum += mono[i];
                if (_win.Count > _box) _winSum -= _win.Dequeue();
                f[i] = (float)(_winSum / _win.Count);
            }
        }
        while (_pos < n - 1)
        {
            int i = (int)Math.Floor(_pos);
            double frac = _pos - i;
            float a = i < 0 ? _last : f[i];
            float b = f[i + 1];
            output.Add((float)(a + (b - a) * frac));
            _pos += _step;
        }
        _pos -= n;
        _last = f[n - 1];
    }
}

