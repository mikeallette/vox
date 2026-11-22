namespace Vox.Core;

/// <summary>
/// Conditions audio before it goes to the speech model. Added 2026-11-07: the test laptop's mic array records quiet and
/// muffled, and at that level Parakeet skipped whole phrases; normalizing recovered them (tested on his recording).
///   1. high-pass at about 80 Hz (removes DC offset, hum and desk rumble that would otherwise set the gain)
///   2. gain so the loud speech peaks near -3 dBFS, measured on the 99.9th percentile so one click cannot set it
///      (gain is capped at +30 dB so near-silence is not blown up into hiss)
/// Strong treble boosting was tested too and made results worse, so it is deliberately not done here.
/// </summary>
internal static class AudioPrep
{
    const float TargetPeak = 0.7f, MaxGain = 31.6f;   // +30 dB

    public static float[] Condition(float[] x)
    {
        if (x.Length == 0) return x;
        var y = new float[x.Length];
        double a = Math.Exp(-2 * Math.PI * 80 / 16000.0), px = 0, py = 0;
        for (int i = 0; i < x.Length; i++) { py = a * (py + x[i] - px); px = x[i]; y[i] = (float)py; }

        // robust peak: 99.9th percentile of |sample| (via a 2048-bin histogram, no full sort)
        const int Bins = 2048;
        var hist = new int[Bins];
        foreach (var v in y) hist[Math.Min(Bins - 1, (int)(Math.Abs(v) * Bins))]++;
        long need = (long)(y.Length * 0.999), seen = 0; int bin = Bins - 1;
        for (int b = 0; b < Bins; b++) { seen += hist[b]; if (seen >= need) { bin = b; break; } }
        float peak = (bin + 1) / (float)Bins;
        float gain = Math.Clamp(TargetPeak / Math.Max(peak, 1e-6f), 0.1f, MaxGain);

        for (int i = 0; i < y.Length; i++) y[i] = Math.Clamp(y[i] * gain, -1f, 1f);
        return y;
    }
}
