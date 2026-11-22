namespace Vox.Core;

/// <summary>
/// Spots a dictation that is far quieter than the user's normal voice (too far from the mic, turned away, wrong mic).
/// Real 2026-11-08 test recordings: normal speech level (90th percentile of 40 ms blocks) 0.013 to 0.039; the one that
/// failed in every model was 0.0007, about 26 dB down. Quiet audio can't be fixed after the fact, so Vox warns while
/// the user is still talking.
/// </summary>
public sealed class LevelWatch
{
    public const float DefaultTypical = 0.02f;   // used until Vox has heard a few dictations
    const float Ratio = 0.25f;                  // warn below a quarter of the usual level (12 dB down)
    const float Floor = 0.003f;                 // never warn above this, whatever the history says
    const int MinBlocks = 75;                   // 3 s of 40 ms blocks before judging (allows a pause before speaking)

    readonly List<float> _blocks = new();
    readonly float _threshold;

    public LevelWatch(float typical) => _threshold = Math.Min(Floor * 4, Math.Max(Floor, typical * Ratio));

    public float Threshold => _threshold;
    public bool IsLow { get; private set; }

    /// <summary>Adds one block's RMS. Returns true when IsLow changed.</summary>
    public bool Push(float rms)
    {
        _blocks.Add(rms);
        if (_blocks.Count < MinBlocks || _blocks.Count % 5 != 0) return false;   // re-judge every 200 ms
        bool low = SpeechLevel() < _threshold;
        if (low == IsLow) return false;
        IsLow = low;
        return true;
    }

    /// <summary>Speech level so far: the 90th percentile of the block levels (ignores pauses).</summary>
    public float SpeechLevel()
    {
        if (_blocks.Count == 0) return 0;
        var s = _blocks.ToArray(); Array.Sort(s);
        return s[(int)(s.Length * 0.9)];
    }

    /// <summary>The user's usual level: the median of recent dictations' speech levels.</summary>
    public static float Typical(IReadOnlyList<float> recent)
    {
        if (recent.Count < 3) return DefaultTypical;
        var s = recent.OrderBy(v => v).ToArray();
        return s[s.Length / 2];
    }
}
