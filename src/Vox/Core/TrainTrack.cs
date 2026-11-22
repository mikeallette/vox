using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NAudio.Wave;

namespace Vox.Core;

/// <summary>
/// Over-the-air test that works even when the laptop's mic cancels its own speakers (echo cancellation):
///   Vox.exe --make-track manifest.tsv outDir [clips]   writes test-track.wav to play on a phone or another PC
///   Vox.exe --mic-listen outDir model.bin [maxSeconds]  listens with the real mic, finds the track's sync chirp,
///                                                        cuts out every clip and scores it like a real dictation
/// </summary>
internal static partial class Trainer
{
    const int ChirpLen = (int)(0.6 * TrainAudio.Rate);

    static float[] Chirp()
    {
        // 0.6 s sweep 400 -> 1800 Hz, faded in and out (easy to find, survives phone speakers and room echo)
        var c = new float[ChirpLen]; double ph = 0;
        for (int i = 0; i < c.Length; i++)
        {
            double f = 400 + 1400.0 * i / c.Length;
            ph += 2 * Math.PI * f / TrainAudio.Rate;
            float fade = Math.Min(1f, Math.Min(i, c.Length - i) / 160f);
            c[i] = (float)(0.8 * Math.Sin(ph)) * fade;
        }
        return c;
    }

    public static void MakeTrack(string[] args)
    {
        string manifest = args[1], outDir = args[2];
        int count = args.Length > 3 && int.TryParse(args[3], out var k) ? k : 36;
        Directory.CreateDirectory(outDir);
        var clips = ReadManifest(manifest);
        var pool = BackgroundPool(clips);
        var levels = new (string name, float peak)[] { ("loud", 0.9f), ("medium", 0.35f), ("soft", 0.12f) };
        var noises = new[] { "none", "fan", "office", "keyboard" };
        var sentences = clips.Where(c => c.Kind == "sentence").ToList();
        int step = Math.Max(1, sentences.Count / Math.Max(1, count));
        var picked = sentences.Where((_, i) => i % step == 1).Take(count).ToList();
        picked.AddRange(clips.Where(c => c.Kind == "paragraph").Skip(3).Take(3));

        var track = new List<float>();
        void Silence(double s) => track.AddRange(new float[(int)(s * TrainAudio.Rate)]);
        Silence(1.0); int startChirp = track.Count; track.AddRange(Chirp()); Silence(1.5);
        var rows = new StringBuilder();
        for (int i = 0; i < picked.Count; i++)
        {
            var c = picked[i]; var lv = levels[i % 3]; var nz = noises[(i / 3) % 4];
            var speech = Pad(Scale(TrainAudio.Load(c.Wav), lv.peak));
            if (nz != "none")
            {
                var bed = TrainAudio.NoiseBed(nz, speech.Length, Hash(c.Wav + nz), pool);
                float g = TrainAudio.SpeechRms(speech) / Math.Max(Rms(bed), 1e-6f) / 2.5f;
                for (int s = 0; s < speech.Length; s++) speech[s] += bed[s] * g;
                float p = TrainAudio.Peak(speech); if (p > 0.98f) for (int s = 0; s < speech.Length; s++) speech[s] *= 0.98f / p;
            }
            rows.Append($"{track.Count}\t{speech.Length}\t{c.Id}\t{c.Category}\t{c.Voice}\ttrack-{lv.name}-{nz}\t{c.Wav}\t{c.Written.Replace("\n", "\\n")}\n");
            track.AddRange(speech); Silence(1.2);
        }
        int endChirp = track.Count;
        track.AddRange(Chirp()); Silence(1.0);
        File.WriteAllText(Path.Combine(outDir, "track.tsv"), $"#length={track.Count}\tstartchirp={startChirp}\tendchirp={endChirp}\n" + rows, new UTF8Encoding(false));
        MicRecorder.SaveWav(Path.Combine(outDir, "test-track.wav"), track.ToArray());
        File.WriteAllText(Path.Combine(outDir, "track-info.txt"), $"test-track.wav: {track.Count / (double)TrainAudio.Rate / 60:0.0} min, {picked.Count} clips\n");
    }

    public static async Task MicListenAsync(string[] args)
    {
        string outDir = args[1], model = args[2];
        int maxSec = args.Length > 3 && int.TryParse(args[3], out var m) ? m : 900;
        var progress = Path.Combine(outDir, "progress-listen.txt");
        try
        {
            var lines = File.ReadAllLines(Path.Combine(outDir, "track.tsv"), Encoding.UTF8);
            var head = lines[0].TrimStart('#').Split('\t').Select(p => p.Split('=')).ToDictionary(p => p[0], p => int.Parse(p[1]));
            int trackLen = head["length"], endChirp = head["endchirp"], startChirp = head["startchirp"];
            var items = lines.Skip(1).Where(l => l.Length > 0).Select(l => l.Split('\t')).ToList();

            var settings = AppSettings.Load();
            var (dict, _) = LoadUserData(settings);
            using var engine = new SpeechEngine();
            await engine.LoadAsync(model);
            string prompt = TextCleaner.BuildPrompt(dict), modelName = Path.GetFileNameWithoutExtension(model);
            await engine.TranscribeAsync(new float[16000], settings.Language, settings.Threads, prompt);

            // ---- listen until the whole track has been heard ----
            var rec = new List<float>(); var gate = new object();
            var mic = new MicRecorder();
            mic.Chunk += c => { lock (gate) rec.AddRange(c); };
            mic.Start(settings.MicrophoneName);
            File.WriteAllText(progress, "LISTENING - press play on test-track.wav now (phone or another computer, about 1 m from the laptop)\n");
            var chirp = Chirp(); int found = -1, searched = 0; double score = 0;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < maxSec)
            {
                await Task.Delay(2000);
                float[] snap; lock (gate) snap = rec.ToArray();
                if (found < 0)
                {
                    int f = FindChirp(snap, Math.Max(0, searched - ChirpLen * 2), snap.Length, chirp, out score);
                    searched = snap.Length;
                    if (score > 0.35) { found = f; File.WriteAllText(progress, $"heard the start chirp (match {score:0.00}) - keep it playing\n"); }
                }
                else if (snap.Length > found + trackLen + TrainAudio.Rate) break;
                else File.WriteAllText(progress, $"recording: {(snap.Length - found) * 100L / trackLen}% of the track\n");
            }
            var all = mic.Stop();
            var sim = Environment.GetEnvironmentVariable("VOX_LISTEN_FILE");   // development check: use a WAV instead of the mic
            if (!string.IsNullOrEmpty(sim))
            {
                var f = TrainAudio.Load(sim); all = new float[f.Length + 3 * TrainAudio.Rate];
                var rnd = new Random(1); for (int i = 0; i < all.Length; i++) all[i] = (float)(rnd.NextDouble() - 0.5) * 0.004f;
                for (int i = 0; i < f.Length; i++) all[i + 3 * TrainAudio.Rate] += f[i] * 0.3f;
                found = FindChirp(all, 0, Math.Min(all.Length, 20 * TrainAudio.Rate), chirp, out score);
            }
            MicRecorder.SaveWav(Path.Combine(outDir, "listen-recording.wav"), all);
            if (found < 0) { File.WriteAllText(progress, "FAILED: never heard the start chirp. Is the track playing and loud enough?\n"); return; }

            // clock drift between the player and the laptop: locate the end chirp
            double ratio = 1;
            int expEnd = found + endChirp - startChirp;
            int fe = FindChirp(all, Math.Max(0, expEnd - TrainAudio.Rate), Math.Min(all.Length, expEnd + TrainAudio.Rate + ChirpLen), chirp, out var endScore);
            if (endScore > 0.35) ratio = (fe - found) / (double)(endChirp - startChirp);

            float floor = Rms(all.Skip(Math.Max(0, found - TrainAudio.Rate)).Take(Math.Min(found, TrainAudio.Rate)).ToArray());
            var cal = new StringBuilder();
            cal.AppendLine($"- Played from another device, heard by: {(string.IsNullOrWhiteSpace(settings.MicrophoneName) ? "Windows default microphone" : settings.MicrophoneName)}");
            cal.AppendLine($"- Sync: start chirp match {score:0.00}, end chirp match {endScore:0.00}, clock ratio {ratio:0.0000}");
            cal.AppendLine($"- Room noise before the track: RMS {floor:0.00000}");
            File.WriteAllText(Path.Combine(outDir, "mic-calibration.md"), cal.ToString());

            // ---- cut out each clip and transcribe it like a dictation ----
            var resultsPath = Path.Combine(outDir, "results-track.jsonl");
            using var outFile = new StreamWriter(resultsPath, append: false, new UTF8Encoding(false));
            int done = 0;
            foreach (var it in items)
            {
                int start = found + (int)((int.Parse(it[0]) - startChirp) * ratio), len = (int)(int.Parse(it[1]) * ratio);
                int a = Math.Max(0, start - TrainAudio.Rate / 4), b = Math.Min(all.Length, start + len + TrainAudio.Rate / 4);
                if (b <= a) continue;
                var seg = all[a..b];
                float peak = 0;                                           // Vox's level meter: loudest 40 ms RMS
                for (int i = 0; i + 640 <= seg.Length; i += 640) { double q = 0; for (int j = 0; j < 640; j++) q += seg[i + j] * seg[i + j]; peak = Math.Max(peak, (float)Math.Sqrt(q / 640)); }
                bool noSound = peak < 0.003;
                var t0 = Stopwatch.StartNew();
                var liveR = noSound ? null : await Transcribe(engine, settings, prompt, seg, "live");
                var wholeR = noSound ? null : await Transcribe(engine, settings, prompt, seg, "whole");
                foreach (var (mode, r0) in new[] { ("live", liveR), ("whole", wholeR) })
                {
                    var r = r0 ?? new Result { Mode = mode, Raw = "", AudioSec = Math.Round(seg.Length / (double)TrainAudio.Rate, 2) };
                    r.Key = $"{modelName}|{it[6]}|{it[5]}|{mode}"; r.Id = it[2]; r.Category = it[3]; r.Kind = "mic"; r.Voice = it[4];
                    r.Condition = it[5]; r.Written = it[7].Replace("\\n", "\n"); r.Peak = peak; r.NoSound = noSound;
                    outFile.WriteLine(JsonSerializer.Serialize(r, Json)); outFile.Flush();
                }
                File.WriteAllText(progress, $"scoring {++done}/{items.Count}\n");
            }
            outFile.Close();
            Learn(outDir);
            File.AppendAllText(progress, "DONE\n");
        }
        catch (Exception ex) { File.AppendAllText(progress, "FAILED: " + ex + "\n"); }
    }

    /// <summary>Normalized cross-correlation search at 4 kHz. Returns the sample index of the best match.</summary>
    static int FindChirp(float[] x, int from, int to, float[] chirp, out double best)
    {
        best = 0; int bestAt = -1;
        float[] Dec(float[] s, int a, int b) { var d = new float[Math.Max(0, (b - a) / 4)]; for (int i = 0; i < d.Length; i++) d[i] = (s[a + 4 * i] + s[a + 4 * i + 1] + s[a + 4 * i + 2] + s[a + 4 * i + 3]) / 4; return d; }
        var c = Dec(chirp, 0, chirp.Length); var y = Dec(x, from, to);
        if (y.Length < c.Length) return -1;
        double cn = Math.Sqrt(c.Sum(v => (double)v * v));
        for (int lag = 0; lag + c.Length <= y.Length; lag++)
        {
            // exact window energy (a running sum drifts after a loud chirp and makes silence look like a perfect match)
            double win = 0, dot = 0;
            for (int i = 0; i < c.Length; i++) { double v = y[lag + i]; win += v * v; dot += c[i] * v; }
            if (win < 1e-6) continue;
            double sc = dot / (cn * Math.Sqrt(win));
            if (sc > best) { best = sc; bestAt = lag; }
        }
        return bestAt < 0 ? -1 : from + bestAt * 4;
    }

    /// <summary>How well the recorded loudness follows the played loudness (0 = not at all, 1 = perfectly).</summary>
    static double EnvelopeMatch(float[] played, float[] recorded)
    {
        double[] Env(float[] s) { var e = new double[s.Length / 320]; for (int i = 0; i < e.Length; i++) { double q = 0; for (int k = 0; k < 320; k++) q += s[i * 320 + k] * s[i * 320 + k]; e[i] = Math.Sqrt(q / 320); } return e; }
        var p = Env(played); var r = Env(recorded); double best = 0;
        for (int lag = 0; lag < 40; lag++)
        {
            int n = Math.Min(p.Length, r.Length - lag); if (n < 20) break;
            double mp = 0, mr = 0; for (int i = 0; i < n; i++) { mp += p[i]; mr += r[i + lag]; } mp /= n; mr /= n;
            double sxy = 0, sxx = 0, syy = 0;
            for (int i = 0; i < n; i++) { double a = p[i] - mp, b = r[i + lag] - mr; sxy += a * b; sxx += a * a; syy += b * b; }
            if (sxx > 0 && syy > 0) best = Math.Max(best, sxy / Math.Sqrt(sxx * syy));
        }
        return best;
    }
}
