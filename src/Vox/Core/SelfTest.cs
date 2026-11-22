using System.Diagnostics;
using NAudio.Wave;

namespace Vox.Core;

/// <summary>
/// Headless check used during development and for support:
///   Vox.exe --selftest input.wav result.txt [model.bin]
/// Transcribes a 16 kHz mono WAV with the local model, runs the cleanup, and writes a small report.
/// </summary>
internal static class SelfTest
{
    /// <summary>Vox.exe --selftest-live input.wav result.txt model.bin : feeds the clip at real speed and measures the wait after it ends.</summary>
    public static async Task RunLiveAsync(string[] args)
    {
        var report = new System.Text.StringBuilder();
        try
        {
            using var engine = new SpeechEngine();
            await engine.LoadAsync(args[3]);
            using var rdr = new WaveFileReader(args[1]);
            var bytes = new byte[rdr.Length]; int read = 0;
            while (read < bytes.Length) { int n = rdr.Read(bytes, read, bytes.Length - read); if (n <= 0) break; read += n; }
            var samples = new float[read / 2];
            for (int i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
            // Real users release the key right after the last word, so cut the clip's trailing silence (keep 180 ms like Vox does).
            // "Silence" is relative to the clip's own loudest 20 ms frame, so quiet recordings are trimmed the same way.
            var frameRms = new List<double>();
            for (int f = 0; f + 320 <= samples.Length; f += 320)
            {
                double s2 = 0; for (int k = 0; k < 320; k++) s2 += samples[f + k] * samples[f + k];
                frameRms.Add(Math.Sqrt(s2 / 320));
            }
            double trimAt = (frameRms.Count > 0 ? frameRms.Max() : 0) * 0.04;
            int last = 0;
            for (int i = 0; i < frameRms.Count; i++) if (frameRms[i] > trimAt) last = (i + 1) * 320;
            samples = samples[..Math.Min(samples.Length, last + 2880)];
            double secs = samples.Length / 16000.0;
            // warm-up so the first call does not include one-time setup
            await engine.TranscribeAsync(new float[16000], "auto", 0, "");

            var live = new LiveTranscriber(engine, "auto", 0, "");
            const int chunk = 640;                           // 40 ms
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < samples.Length; i += chunk)
            {
                live.Push(samples.AsSpan(i, Math.Min(chunk, samples.Length - i)).ToArray());
                var due = TimeSpan.FromSeconds((i + chunk) / 16000.0);
                var wait = due - clock.Elapsed; if (wait > TimeSpan.Zero) await Task.Delay(wait);
            }
            int phrases = live.SegmentCount;
            var sw = Stopwatch.StartNew();                   // "key released" now
            var raw = await live.FinishAsync();
            double waitMs = sw.Elapsed.TotalMilliseconds;

            var sw2 = Stopwatch.StartNew();
            var whole = await engine.TranscribeAsync(AudioPrep.Condition(samples), "auto", 0, "");   // same as Vox's whole-clip path
            double wholeMs = sw2.Elapsed.TotalMilliseconds;

            report.AppendLine($"audio: {secs:0.0} s   phrases already sent while speaking: {phrases}");
            report.AppendLine($"wait after release (live):      {waitMs:0} ms");
            report.AppendLine($"wait after release (whole clip): {wholeMs:0} ms");
            report.AppendLine("LIVE : " + raw);
            report.AppendLine("WHOLE: " + whole);
            report.AppendLine("OK");
        }
        catch (Exception ex) { report.AppendLine("FAILED: " + ex); }
        File.WriteAllText(args[2], report.ToString());
    }

    public static async Task RunAsync(string[] args)
    {
        var wav = args[1]; var outPath = args[2];
        var report = new System.Text.StringBuilder();
        try
        {
            var settings = new AppSettings { Language = "auto" };
            string modelPath = args.Length > 3 ? args[3] : "";
            if (string.IsNullOrEmpty(modelPath))
            {
                var dir = Path.Combine(AppContext.BaseDirectory, "models");
                modelPath = Directory.Exists(dir) ? Directory.GetFiles(dir, "ggml-*.bin").Where(f => !Path.GetFileName(f).StartsWith("ggml-silero", StringComparison.OrdinalIgnoreCase)).OrderBy(f => new FileInfo(f).Length).FirstOrDefault() ?? "" : "";
            }
            if (!File.Exists(modelPath)) throw new FileNotFoundException("No model found. Pass one as the 3rd argument.");

            using var engine = new SpeechEngine();
            var swLoad = Stopwatch.StartNew();
            await engine.LoadAsync(modelPath);
            report.AppendLine($"model: {Path.GetFileName(modelPath)}  load: {swLoad.ElapsedMilliseconds} ms");

            using var rdr = new WaveFileReader(wav);
            if (rdr.WaveFormat.SampleRate != 16000 || rdr.WaveFormat.Channels != 1 || rdr.WaveFormat.BitsPerSample != 16)
                throw new InvalidOperationException($"WAV must be 16 kHz mono 16-bit (got {rdr.WaveFormat}).");
            var bytes = new byte[rdr.Length]; int read = 0;
            while (read < bytes.Length) { int n = rdr.Read(bytes, read, bytes.Length - read); if (n <= 0) break; read += n; }
            var samples = new float[read / 2];
            for (int i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
            double secs = samples.Length / 16000.0;

            int threads = int.TryParse(Environment.GetEnvironmentVariable("VOX_THREADS"), out var t) ? t : 0;
            int runs = int.TryParse(Environment.GetEnvironmentVariable("VOX_RUNS"), out var r) ? r : 1;
            report.AppendLine("native runtime: " + Whisper.net.LibraryLoader.RuntimeOptions.LoadedLibrary + "   cpu threads: " + Environment.ProcessorCount + "   using: " + (threads == 0 ? "auto" : threads.ToString()));
            string raw = ""; double ms = 0;
            for (int i = 0; i < runs; i++)
            {
                var sw = Stopwatch.StartNew();
                raw = await engine.TranscribeAsync(Environment.GetEnvironmentVariable("VOX_NOCOND") == "1" ? samples : AudioPrep.Condition(samples), "auto", threads, "");
                ms = sw.Elapsed.TotalMilliseconds;
                if (runs > 1) report.AppendLine($"  run {i + 1}: {ms:0} ms");
            }
            var clean = TextCleaner.Clean(raw, settings, Array.Empty<DictEntry>(), Array.Empty<Snippet>());
            report.AppendLine($"audio: {secs:0.0} s  transcribe: {ms:0} ms  realtime factor: {ms / 1000.0 / secs:0.00}x");
            report.AppendLine("RAW: " + raw);
            report.AppendLine("CLEAN: " + clean);
            report.AppendLine("OK");
        }
        catch (Exception ex) { report.AppendLine("FAILED: " + ex); }
        File.WriteAllText(outPath, report.ToString());
    }
}
