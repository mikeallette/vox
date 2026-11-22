using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Vox.Core;

/// <summary>
/// Test-condition audio for the trainer: volume levels, noisy places and rooms, all made offline from clean speech.
/// Every condition is deterministic for a given seed, so a re-run produces the same audio and results are comparable.
/// </summary>
internal static class TrainAudio
{
    public const int Rate = 16000;

    /// <summary>What each condition imitates (shown in the report).</summary>
    public static readonly Dictionary<string, string> Describe = new()
    {
        ["studio"] = "clean speech, healthy level (baseline)",
        ["quiet-mic"] = "quiet microphone, peak 0.045 (the level measured on a real laptop mic)",
        ["very-quiet"] = "very quiet microphone, peak 0.015, with mic self-noise",
        ["too-loud"] = "input gain far too high, hard clipping",
        ["far-room"] = "talking from across a room: echo, thin sound, low level, light fan noise",
        ["headset"] = "cheap headset / phone band (300 Hz to 3.4 kHz) with hiss",
        ["fan"] = "fan or HVAC noise, 10 dB below the speech",
        ["office"] = "office chatter: three other people talking in the background",
        ["tv"] = "TV or a coworker talking nearby (one competing voice, 6 dB below)",
        ["keyboard"] = "someone typing, plus light room noise",
        ["hum"] = "electrical hum (60 Hz and harmonics) and hiss, like a bad USB mic or ground loop",
        ["car"] = "in a car: heavy low road rumble, 4 dB below the speech",
        ["bluetooth"] = "Bluetooth headset: narrow phone band, coarse 8-bit sound, short dropouts",
        ["kitchen"] = "kitchen: dishes clinking and running-water hiss",
    };

    public static readonly string[] LevelAndRoom = { "quiet-mic", "far-room", "very-quiet", "headset", "too-loud" };
    public static readonly string[] Noises = { "fan", "office", "tv", "keyboard", "hum" };
    /// <summary>Added for the overnight loop (not part of the first runs, so earlier results stay comparable).</summary>
    public static readonly string[] Extra = { "car", "bluetooth", "kitchen" };

    // ---------- file IO ----------

    /// <summary>Any WAV (any rate, mono or stereo) -> 16 kHz mono float.</summary>
    public static float[] Load(string path)
    {
        using var rdr = new WaveFileReader(path);
        ISampleProvider sp = rdr.ToSampleProvider();
        if (sp.WaveFormat.Channels == 2) sp = new StereoToMonoSampleProvider(sp);
        if (sp.WaveFormat.SampleRate != Rate) sp = new WdlResamplingSampleProvider(sp, Rate);
        var all = new List<float>((int)(rdr.Length / 2));
        var buf = new float[8192]; int n;
        while ((n = sp.Read(buf, 0, buf.Length)) > 0) for (int i = 0; i < n; i++) all.Add(buf[i]);
        return all.ToArray();
    }

    // ---------- the conditions ----------

    /// <param name="others">other speech clips, used as background voices for "office" and "tv"</param>
    public static float[] Make(string condition, float[] speech, int seed, Func<int, float[]> others)
    {
        var rng = new Random(seed);
        int lead = (int)(0.35 * Rate), tail = (int)(0.3 * Rate);
        var x = new float[lead + speech.Length + tail];
        Array.Copy(Normalize(speech, 0.5f), 0, x, lead, speech.Length);
        float s = SpeechRms(x);

        switch (condition)
        {
            case "studio":
                Mix(x, White(x.Length, rng), s, 50); return Level(x, 0.5f);
            case "quiet-mic":
                Mix(x, Pink(x.Length, rng), s, 35); return Level(x, 0.045f);
            case "very-quiet":
                x = Level(x, 0.015f); Mix(x, White(x.Length, rng), SpeechRms(x), 22); return x;
            case "too-loud":
                Mix(x, Pink(x.Length, rng), s, 35);
                for (int i = 0; i < x.Length; i++) x[i] = Math.Clamp(x[i] * 5f, -1f, 1f);
                return Quantize(x);
            case "far-room":
                x = Reverb(x, 0.45f); HighPass(x, 200); LowPass(x, 5000);
                Mix(x, Pink(x.Length, rng), SpeechRms(x), 16); return Level(x, 0.04f);
            case "headset":
                HighPass(x, 300); HighPass(x, 300); LowPass(x, 3400); LowPass(x, 3400);
                Mix(x, White(x.Length, rng), SpeechRms(x), 25); return Level(x, 0.3f);
            case "fan":
                Mix(x, Pink(x.Length, rng), s, 10); return Level(x, 0.3f);
            case "office":
            {
                var b = new float[x.Length];
                for (int k = 0; k < 3; k++) AddLooped(b, others(rng.Next()), rng.Next(Rate * 2), 1f);
                Mix(x, b, s, 9); Mix(x, Pink(x.Length, rng), s, 25); return Level(x, 0.3f);
            }
            case "tv":
            {
                var b = new float[x.Length];
                AddLooped(b, others(rng.Next()), rng.Next(Rate), 1f);
                LowPass(b, 6000);
                Mix(x, b, s, 6); return Level(x, 0.3f);
            }
            case "keyboard":
                Mix(x, Keyboard(x.Length, rng), s, 8); Mix(x, Pink(x.Length, rng), s, 28); return Level(x, 0.3f);
            case "hum":
                Mix(x, Hum(x.Length, rng), s, 12); Mix(x, White(x.Length, rng), s, 24); return Level(x, 0.3f);
            case "car":
                Mix(x, Rumble(x.Length, rng), s, 4); Mix(x, Pink(x.Length, rng), s, 20); return Level(x, 0.3f);
            case "bluetooth":
            {
                HighPass(x, 250); LowPass(x, 3500); LowPass(x, 3500);
                Mix(x, White(x.Length, rng), SpeechRms(x), 30);
                x = Normalize(x, 0.6f);
                for (int i = 0; i < x.Length; i++)                     // coarse codec: 8-bit mu-law style steps
                {
                    double v = Math.Sign(x[i]) * Math.Log(1 + 255 * Math.Abs(x[i])) / Math.Log(256);
                    v = Math.Round(v * 127) / 127;
                    x[i] = (float)(Math.Sign(v) * (Math.Pow(256, Math.Abs(v)) - 1) / 255);
                }
                for (int i = 0; i < x.Length; i += 320)                // 20 ms packets, about 2% dropped
                    if (rng.NextDouble() < 0.02) Array.Clear(x, i, Math.Min(320, x.Length - i));
                return Level(x, 0.3f);
            }
            case "kitchen":
                Mix(x, Clinks(x.Length, rng), s, 6); Mix(x, Water(x.Length, rng), s, 14); return Level(x, 0.3f);
            default: throw new ArgumentException("unknown condition " + condition);
        }
    }

    /// <summary>Background noise alone (for the over-the-air mic test, played next to the speech).</summary>
    public static float[] NoiseBed(string kind, int length, int seed, Func<int, float[]> others)
    {
        var rng = new Random(seed);
        var b = new float[length];
        switch (kind)
        {
            case "fan": b = Pink(length, rng); break;
            case "office": for (int k = 0; k < 3; k++) AddLooped(b, others(rng.Next()), rng.Next(Rate * 2), 1f); break;
            case "keyboard": b = Keyboard(length, rng); break;
            case "hum": b = Hum(length, rng); break;
            case "car": b = Rumble(length, rng); break;
            case "kitchen": b = Clinks(length, rng); var w = Water(length, rng); for (int i = 0; i < length; i++) b[i] += 0.3f * w[i]; break;
            case "room": b = Pink(length, rng); break;
            case "white": b = White(length, rng); break;
        }
        return b;
    }

    /// <summary>Scale to a peak level and round to 16-bit (for the noise-only tests).</summary>
    public static float[] AtPeak(float[] x, float peak) => Level(x, peak);

    static float[] Rumble(int n, Random r)
    {
        // leaky-integrated white noise, low-passed: road and engine rumble
        var y = new float[n]; double acc = 0;
        for (int i = 0; i < n; i++) { acc = 0.995 * acc + (r.NextDouble() * 2 - 1); y[i] = (float)acc; }
        LowPass(y, 350); LowPass(y, 350);
        return y;
    }

    static float[] Clinks(int n, Random r)
    {
        // dishes and cutlery: short ringing tones at random times
        var y = new float[n]; int i = r.Next(Rate / 3);
        while (i < n)
        {
            double f = 1800 + r.NextDouble() * 4500, a = 0.3 + r.NextDouble() * 0.7, ph = r.NextDouble() * 6.28;
            int len = Rate / 8 + r.Next(Rate / 6);
            for (int k = 0; k < len && i + k < n; k++) y[i + k] += (float)(a * Math.Sin(2 * Math.PI * f * k / Rate + ph) * Math.Exp(-k / (Rate * 0.03)));
            i += r.Next(Rate / 4, Rate * 3 / 2);
        }
        return y;
    }

    static float[] Water(int n, Random r) { var y = White(n, r); HighPass(y, 1500); return y; }

    // ---------- helpers ----------

    public static float Peak(float[] x) { float p = 0; foreach (var v in x) p = Math.Max(p, Math.Abs(v)); return p; }

    static float[] Normalize(float[] x, float peak)
    {
        float p = Peak(x); var y = new float[x.Length];
        float g = p > 0 ? peak / p : 0; for (int i = 0; i < x.Length; i++) y[i] = x[i] * g; return y;
    }

    /// <summary>Scale to a peak level, then round to 16-bit like a real recording.</summary>
    static float[] Level(float[] x, float peak) => Quantize(Normalize(x, peak));

    static float[] Quantize(float[] x)
    {
        for (int i = 0; i < x.Length; i++) x[i] = MathF.Round(Math.Clamp(x[i], -1f, 1f) * 32767f) / 32767f;
        return x;
    }

    /// <summary>RMS over the louder frames only (speech, not the pauses).</summary>
    public static float SpeechRms(float[] x)
    {
        var f = new List<double>();
        for (int i = 0; i + 320 <= x.Length; i += 320) { double q = 0; for (int k = 0; k < 320; k++) q += x[i + k] * x[i + k]; f.Add(q / 320); }
        if (f.Count == 0) return 0;
        double max = f.Max(); var loud = f.Where(v => v > max * 0.01).ToList();
        return (float)Math.Sqrt(loud.Average());
    }

    static float Rms(float[] x) { double q = 0; foreach (var v in x) q += v * v; return x.Length == 0 ? 0 : (float)Math.Sqrt(q / x.Length); }

    /// <summary>Adds noise so it sits snrDb below the given speech RMS.</summary>
    static void Mix(float[] x, float[] noise, float speechRms, double snrDb)
    {
        float r = Rms(noise); if (r <= 0) return;
        float g = (float)(speechRms / Math.Pow(10, snrDb / 20) / r);
        for (int i = 0; i < x.Length; i++) x[i] += noise[i] * g;
    }

    static void AddLooped(float[] dst, float[] src, int offset, float gain)
    {
        if (src.Length == 0) return;
        for (int i = 0; i < dst.Length; i++) dst[i] += src[(i + offset) % src.Length] * gain;
    }

    static float[] White(int n, Random r) { var y = new float[n]; for (int i = 0; i < n; i++) y[i] = (float)(r.NextDouble() * 2 - 1); return y; }

    static float[] Pink(int n, Random r)
    {
        // Paul Kellet's refined pink filter on white noise (fan / HVAC / road rumble character)
        var y = new float[n]; double b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0, b5 = 0, b6 = 0;
        for (int i = 0; i < n; i++)
        {
            double w = r.NextDouble() * 2 - 1;
            b0 = 0.99886 * b0 + w * 0.0555179; b1 = 0.99332 * b1 + w * 0.0750759; b2 = 0.96900 * b2 + w * 0.1538520;
            b3 = 0.86650 * b3 + w * 0.3104856; b4 = 0.55000 * b4 + w * 0.5329522; b5 = -0.7616 * b5 - w * 0.0168980;
            y[i] = (float)(b0 + b1 + b2 + b3 + b4 + b5 + b6 + w * 0.5362); b6 = w * 0.115926;
        }
        return y;
    }

    static float[] Hum(int n, Random r)
    {
        var y = new float[n]; double ph = r.NextDouble() * 6.28;
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)Rate;
            y[i] = (float)(Math.Sin(2 * Math.PI * 60 * t + ph) + 0.6 * Math.Sin(2 * Math.PI * 120 * t) + 0.45 * Math.Sin(2 * Math.PI * 180 * t) + 0.25 * Math.Sin(2 * Math.PI * 300 * t));
        }
        return y;
    }

    static float[] Keyboard(int n, Random r)
    {
        // bursts of typing: key clicks every 70-260 ms, short pauses between bursts
        var y = new float[n]; int i = r.Next(Rate / 4);
        while (i < n)
        {
            float a = 0.4f + (float)r.NextDouble() * 0.6f;
            int len = 80 + r.Next(80);
            for (int k = 0; k < len && i + k < n; k++) y[i + k] += a * (float)(r.NextDouble() * 2 - 1) * MathF.Exp(-k / 25f);
            i += r.Next(1100, 4200);
            if (r.NextDouble() < 0.08) i += r.Next(Rate / 2, Rate * 2);
        }
        return y;
    }

    static float[] Reverb(float[] x, float wet)
    {
        // small Schroeder room: four combs into two all-passes
        int[] combs = { 557, 617, 491, 422 }; float[] fb = { 0.80f, 0.78f, 0.76f, 0.74f };
        var outp = new float[x.Length];
        for (int c = 0; c < combs.Length; c++)
        {
            var buf = new float[combs[c] * 3]; int d = buf.Length, p = 0;
            for (int i = 0; i < x.Length; i++) { float o = buf[p]; buf[p] = x[i] + o * fb[c]; p = (p + 1) % d; outp[i] += o * 0.25f; }
        }
        foreach (var d in new[] { 225, 556 })
        {
            var buf = new float[d]; int p = 0;
            for (int i = 0; i < outp.Length; i++) { float b = buf[p]; float v = outp[i] + b * 0.5f; buf[p] = v; outp[i] = b - v * 0.5f; p = (p + 1) % d; }
        }
        var y = new float[x.Length];
        for (int i = 0; i < x.Length; i++) y[i] = x[i] * (1 - wet) + outp[i] * wet * 2.2f;
        return y;
    }

    static void LowPass(float[] x, double fc) => Biquad(x, fc, false);
    static void HighPass(float[] x, double fc) => Biquad(x, fc, true);

    static void Biquad(float[] x, double fc, bool high)
    {
        // RBJ cookbook, Q = 0.707
        double w = 2 * Math.PI * fc / Rate, cs = Math.Cos(w), al = Math.Sin(w) / (2 * 0.7071);
        double b0 = high ? (1 + cs) / 2 : (1 - cs) / 2, b1 = high ? -(1 + cs) : 1 - cs, b2 = b0;
        double a0 = 1 + al, a1 = -2 * cs, a2 = 1 - al;
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double y = (b0 * x[i] + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2) / a0;
            x2 = x1; x1 = x[i]; y2 = y1; y1 = y; x[i] = (float)y;
        }
    }
}
