using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NAudio.Wave;

namespace Vox.Core;

/// <summary>
/// Hands-off testing and "training" for Vox. Nobody has to talk:
///   Vox.exe --train    manifest.tsv outDir model.bin [full]   synthetic voices x volume levels x noisy places, straight into the engine
///   Vox.exe --mic-test manifest.tsv outDir model.bin [clips]  plays clips out of the speakers and records them with the real mic path
///   Vox.exe --learn    outDir                                 re-scores everything and learns corrections (no audio work)
///
/// The speech models can't be fine-tuned on a laptop CPU (Parakeet has no prompt either), so Vox "learns" the way it
/// can: it finds the words the engine keeps getting wrong for this vocabulary and proposes "heard as -> write as"
/// dictionary entries. A correction is kept only if it fixes errors in at least two recordings, makes no recording
/// worse, and does not change any correct sentence in the corpus.
/// </summary>
internal static partial class Trainer
{
    sealed record Clip(string Id, string Category, string Kind, string Voice, string Wav, string Written);

    sealed class Result
    {
        public string Key { get; set; } = "";
        public string Id { get; set; } = "";
        public string Category { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Voice { get; set; } = "";
        public string Condition { get; set; } = "";
        public string Mode { get; set; } = "";
        public string Written { get; set; } = "";
        public string Raw { get; set; } = "";
        public double AudioSec { get; set; }
        public double Ms { get; set; }
        public float Peak { get; set; }
        public bool NoSound { get; set; }
        // scoring state (not saved)
        [System.Text.Json.Serialization.JsonIgnore] public string BaseClean = "", Clean = "";
        [System.Text.Json.Serialization.JsonIgnore] public int RefWords, BaseErr, BaseErrCs, Err, ErrCs;
    }

    static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // =====================================================================================================
    // 1. synthetic: voices x conditions, fed straight to the engine
    // =====================================================================================================
    public static async Task TrainAsync(string[] args)
    {
        string manifest = args[1], outDir = args[2], model = args[3];
        bool full = args.Length > 4 && args[4] == "full";
        bool round = args.Length > 4 && args[4] == "round";       // overnight loop: studio + two random conditions, new ones included
        var allNoisy = TrainAudio.Describe.Keys.Where(k => k != "studio").ToArray();
        Directory.CreateDirectory(outDir);
        var progress = Path.Combine(outDir, "progress.txt");
        try
        {
            var clips = ReadManifest(manifest);
            var settings = AppSettings.Load();
            var (dict, _) = LoadUserData(settings);
            using var engine = new SpeechEngine();
            await engine.LoadAsync(model);
            string prompt = TextCleaner.BuildPrompt(dict), modelName = Path.GetFileNameWithoutExtension(model);
            await engine.TranscribeAsync(new float[16000], settings.Language, settings.Threads, prompt);   // warm-up

            var resultsPath = Path.Combine(outDir, "results-synthetic.jsonl");
            var done = ReadResults(resultsPath).ToDictionary(r => r.Key);
            var pool = BackgroundPool(clips);

            // the plan: every clip clean, plus one level/room condition and one noise condition (or all of them with "full")
            var jobs = new List<(Clip c, string cond, string mode)>();
            for (int i = 0; i < clips.Count; i++)
            {
                var c = clips[i];
                if (c.Kind == "paragraph")
                {
                    foreach (var cond in round ? new[] { "studio", "quiet-mic", "car", "kitchen", "bluetooth" } : new[] { "studio", "quiet-mic", "far-room", "office", "fan" })
                        foreach (var mode in new[] { "whole", "live" }) jobs.Add((c, cond, mode));
                    continue;
                }
                int h1 = Hash(c.Wav), h2 = Hash(c.Wav + "#2");
                string pickA = allNoisy[h1 % allNoisy.Length], pickB = allNoisy[(h1 % allNoisy.Length + 1 + h2 % (allNoisy.Length - 1)) % allNoisy.Length];
                var conds = full ? TrainAudio.Describe.Keys.ToArray()
                          : round ? new[] { "studio", pickA, pickB }
                                 : new[] { "studio", TrainAudio.LevelAndRoom[i % 5], TrainAudio.Noises[(i * 2 + i / 5) % 5] };
                foreach (var cond in conds) jobs.Add((c, cond, "whole"));
            }

            var sw = Stopwatch.StartNew(); int n = 0, fresh = 0;
            using var outFile = new StreamWriter(resultsPath, append: true, new UTF8Encoding(false));
            foreach (var (c, cond, mode) in jobs)
            {
                n++;
                var key = $"{modelName}|{c.Wav}|{cond}|{mode}";
                if (done.ContainsKey(key)) continue;
                var clean = TrainAudio.Load(c.Wav);
                var audio = TrainAudio.Make(cond, clean, Hash(c.Wav + cond), pool);
                var r = await Transcribe(engine, settings, prompt, audio, mode);
                r.Key = key; r.Id = c.Id; r.Category = c.Category; r.Kind = c.Kind; r.Voice = c.Voice; r.Condition = cond; r.Written = c.Written;
                outFile.WriteLine(JsonSerializer.Serialize(r, Json)); outFile.Flush();
                fresh++;
                if (fresh % 10 == 0)
                {
                    double per = sw.Elapsed.TotalSeconds / fresh;
                    File.WriteAllText(progress, $"{n}/{jobs.Count} transcriptions, {per:0.00} s each, about {(jobs.Count - n) * per / 60:0} min left\n");
                }
            }
            outFile.Close();
            File.WriteAllText(progress, $"transcribed {fresh} new clips ({jobs.Count} planned) in {sw.Elapsed.TotalMinutes:0.0} min\n");
            Learn(outDir);
            File.AppendAllText(progress, "DONE\n");
        }
        catch (Exception ex) { File.AppendAllText(progress, "FAILED: " + ex + "\n"); }
    }

    // =====================================================================================================
    // 2. over the air: speakers -> room -> the real microphone, through Vox's own recorder and live transcriber
    // =====================================================================================================
    public static async Task MicTestAsync(string[] args)
    {
        string manifest = args[1], outDir = args[2], model = args[3];
        int count = args.Length > 4 && int.TryParse(args[4], out var k) ? k : 36;
        var micDir = Path.Combine(outDir, "mic");
        Directory.CreateDirectory(micDir);
        var progress = Path.Combine(outDir, "progress-mic.txt");
        var cal = new StringBuilder();
        try
        {
            var clips = ReadManifest(manifest);
            var settings = AppSettings.Load();
            var (dict, _) = LoadUserData(settings);
            using var engine = new SpeechEngine();
            await engine.LoadAsync(model);
            string prompt = TextCleaner.BuildPrompt(dict), modelName = Path.GetFileNameWithoutExtension(model);
            await engine.TranscribeAsync(new float[16000], settings.Language, settings.Threads, prompt);
            var pool = BackgroundPool(clips);
            string micName = string.IsNullOrWhiteSpace(settings.MicrophoneName) ? "Windows default microphone" : settings.MicrophoneName;

            // --- calibration: how loud is the room, and how well does the mic hear the speakers? ---
            var silence = await PlayAndRecord(new float[TrainAudio.Rate * 2], settings, null);
            var first = clips.First(c => c.Kind == "sentence");
            var probe = Scale(TrainAudio.Load(first.Wav), 0.9f);
            var heard = await PlayAndRecord(Pad(probe), settings, null);
            float roomRms = TrainAudio.SpeechRms(silence.samples), heardRms = TrainAudio.SpeechRms(heard.samples);
            double snr = 20 * Math.Log10(Math.Max(heardRms, 1e-6) / Math.Max(Rms(silence.samples), 1e-6));
            cal.AppendLine($"- Microphone: {micName}");
            cal.AppendLine($"- Room noise on the mic (nothing playing): RMS {Rms(silence.samples):0.00000}, Vox peak meter {silence.peak:0.0000}");
            cal.AppendLine($"- Speakers at full test level: Vox peak meter {heard.peak:0.0000}, speech about {snr:0} dB above the room noise");
            // Laptop mic arrays often run echo cancellation, which removes whatever the laptop itself plays.
            // Then the recording does not follow the speech at all, and every result would be meaningless.
            double follow = EnvelopeMatch(Pad(probe), heard.samples);
            cal.AppendLine($"- How closely the recording follows the played speech: {follow:0.00} (1.00 = perfectly)");
            bool cancelled = heard.peak >= 0.003 && follow < 0.45;
            cal.AppendLine(heard.peak < 0.003
                ? "- **The mic cannot hear the speakers.** Turn the Windows volume up (or use external speakers) and run the mic test again."
                : cancelled ? "- **The microphone's echo cancellation is removing the laptop's own speaker sound**, so this test cannot work on this PC as is. Use the test track instead (play it from a phone): `Run-Training.ps1 -PhoneTrack`."
                : heard.peak < 0.02 ? "- The mic hears the speakers only faintly, so this test is a hard case (like talking quietly or from a distance)."
                : "- The mic hears the speakers well.");
            File.WriteAllText(Path.Combine(outDir, "mic-calibration.md"), cal.ToString());
            if (heard.peak < 0.003 || cancelled) { File.WriteAllText(progress, "STOPPED:\n" + cal); return; }

            // --- the plan: spread the clips over playback volumes and background noises ---
            var levels = new (string name, float peak)[] { ("loud", 0.9f), ("medium", 0.35f), ("soft", 0.12f) };
            var noises = new[] { "none", "fan", "office", "keyboard" };
            var sentences = clips.Where(c => c.Kind == "sentence").ToList();
            int step = Math.Max(1, sentences.Count / Math.Max(1, count));
            var picked = sentences.Where((_, i) => i % step == 0).Take(count).ToList();
            picked.AddRange(clips.Where(c => c.Kind == "paragraph").Take(3));

            var resultsPath = Path.Combine(outDir, "results-mic.jsonl");
            var done = ReadResults(resultsPath).Select(r => r.Key).ToHashSet();
            using var outFile = new StreamWriter(resultsPath, append: true, new UTF8Encoding(false));
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < picked.Count; i++)
            {
                var c = picked[i];
                var lv = levels[i % 3]; var nz = noises[(i / 3) % 4];
                var cond = $"air-{lv.name}-{nz}";
                var key = $"{modelName}|{c.Wav}|{cond}";
                if (done.Contains(key + "|live")) continue;
                File.WriteAllText(progress, $"{i + 1}/{picked.Count} playing {Path.GetFileName(c.Wav)} ({cond}), {sw.Elapsed.TotalMinutes:0.0} min so far\n");

                var speech = Pad(Scale(TrainAudio.Load(c.Wav), lv.peak));
                if (nz != "none")
                {
                    var bed = TrainAudio.NoiseBed(nz, speech.Length, Hash(key), pool);
                    float g = TrainAudio.SpeechRms(speech) / Math.Max(Rms(bed), 1e-6f) / 2.5f;   // noise 8 dB under the speech
                    for (int s = 0; s < speech.Length; s++) speech[s] += bed[s] * g;
                    float p = TrainAudio.Peak(speech); if (p > 0.98f) for (int s = 0; s < speech.Length; s++) speech[s] *= 0.98f / p;
                }

                // Live: phrases go to the engine while the clip is still playing, exactly like a held hotkey.
                var live = new LiveTranscriber(engine, settings.Language, settings.Threads, prompt);
                var rec = await PlayAndRecord(speech, settings, live);
                var wavOut = Path.Combine(micDir, $"{Path.GetFileNameWithoutExtension(c.Wav)}-{cond}.wav");
                MicRecorder.SaveWav(wavOut, rec.samples);
                double secs = rec.samples.Length / (double)TrainAudio.Rate;
                bool noSound = rec.peak < 0.003;   // DictationController shows "No sound heard" and types nothing

                var t0 = Stopwatch.StartNew();
                string? liveRaw = noSound ? "" : await live.FinishAsync();
                double liveMs = t0.Elapsed.TotalMilliseconds;
                t0.Restart();
                string wholeRaw = noSound ? "" : await engine.TranscribeAsync(rec.samples, settings.Language, settings.Threads, prompt);
                double wholeMs = t0.Elapsed.TotalMilliseconds;
                liveRaw ??= wholeRaw;

                foreach (var (mode, raw, ms) in new[] { ("live", liveRaw, liveMs), ("whole", wholeRaw, wholeMs) })
                {
                    var r = new Result
                    {
                        Key = key + "|" + mode, Id = c.Id, Category = c.Category, Kind = "mic", Voice = c.Voice, Condition = cond, Mode = mode,
                        Written = c.Written, Raw = raw, AudioSec = Math.Round(secs, 2), Ms = Math.Round(ms), Peak = rec.peak, NoSound = noSound,
                    };
                    outFile.WriteLine(JsonSerializer.Serialize(r, Json)); outFile.Flush();
                }
            }
            outFile.Close();
            File.WriteAllText(progress, $"played {picked.Count} clips in {sw.Elapsed.TotalMinutes:0.0} min\n");
            Learn(outDir);
            File.AppendAllText(progress, "DONE\n");
        }
        catch (Exception ex) { File.AppendAllText(progress, "FAILED: " + ex + "\n"); }
    }

    static async Task<(float[] samples, float peak)> PlayAndRecord(float[] signal, AppSettings settings, LiveTranscriber? live)
    {
        var mic = new MicRecorder();
        if (live != null) mic.Chunk += live.Push;
        mic.Start(settings.MicrophoneName);
        await Task.Delay(250);
        var bytes = new byte[signal.Length * 2];
        for (int i = 0; i < signal.Length; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 2), (short)Math.Clamp(signal[i] * 32767f, -32768, 32767));
        using (var src = new RawSourceWaveStream(new MemoryStream(bytes), new WaveFormat(TrainAudio.Rate, 16, 1)))
        using (var outDev = new WaveOutEvent())
        {
            var end = new TaskCompletionSource();
            outDev.PlaybackStopped += (_, _) => end.TrySetResult();
            outDev.Init(src);
            outDev.Play();
            await Task.WhenAny(end.Task, Task.Delay(TimeSpan.FromSeconds(signal.Length / (double)TrainAudio.Rate + 10)));
        }
        await Task.Delay(180);            // Vox keeps the same tail after the key is released
        var samples = mic.Stop();
        return (samples, mic.PeakLevel);
    }

    // =====================================================================================================
    // shared: transcription, scoring, learning, report
    // =====================================================================================================
    static async Task<Result> Transcribe(SpeechEngine engine, AppSettings s, string prompt, float[] audio, string mode)
    {
        var sw = Stopwatch.StartNew();
        string? raw = null;
        if (mode == "live")
        {
            var live = new LiveTranscriber(engine, s.Language, s.Threads, prompt);
            for (int i = 0; i < audio.Length; i += 640) live.Push(audio.AsSpan(i, Math.Min(640, audio.Length - i)).ToArray());
            raw = await live.FinishAsync();
        }
        raw ??= await SpeechGate.TranscribeAsync(engine, audio, s.Language, s.Threads, prompt, condition: false);   // passes straight through when the gate is off
        return new Result { Mode = mode, Raw = raw, AudioSec = Math.Round(audio.Length / (double)TrainAudio.Rate, 2), Ms = Math.Round(sw.Elapsed.TotalMilliseconds), Peak = TrainAudio.Peak(audio) };
    }

    public static void Learn(string outDir, string? manifest = null)
    {
        var settings = AppSettings.Load();
        var (existing, snippets) = LoadUserData(settings);
        var results = Directory.GetFiles(outDir, "results-*.jsonl").SelectMany(ReadResults).ToList();
        if (results.Count == 0) throw new InvalidOperationException("No results in " + outDir);
        if (manifest != null)   // the corpus text was edited: score against the current wording (audio is unchanged)
        {
            var now = ReadManifest(manifest).GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First().Written);
            foreach (var r in results) if (now.TryGetValue(r.Id, out var w)) r.Written = w;
        }

        // baseline with the user's current dictionary
        foreach (var r in results)
        {
            r.BaseClean = r.Clean = r.NoSound ? "" : TextCleaner.Clean(r.Raw, settings, existing, snippets);
            (r.RefWords, r.BaseErr, r.BaseErrCs) = Score(r.Written, r.Clean);
            r.Err = r.BaseErr; r.ErrCs = r.BaseErrCs;
        }

        var refs = results.Select(r => r.Written).Distinct().ToList();
        var dict = existing.ToList();
        var learned = new List<(string from, string to, int fixedIn, int wordsFixed, string example)>();
        var tried = new HashSet<string>();
        var review = new List<(string from, string to, int support)>();
        var refTokens = refs.SelectMany(Tokenize).ToList();
        var refLower = refTokens.Select(t => t.Norm).ToHashSet();
        var refCased = refTokens.Select(t => t.Cased).ToHashSet();

        for (int pass = 0; pass < 3; pass++)
        {
            var cands = Mine(results).Where(c => !tried.Contains(c.Key)).OrderByDescending(c => c.Value.support).ThenByDescending(c => c.Value.from.Length).Take(600).ToList();
            int acceptedThisPass = 0;
            foreach (var (ckey, c) in cands)
            {
                tried.Add(ckey);
                if (c.support < 2) continue;
                if (c.to.Contains('$')) continue;                                          // TextCleaner treats $ in "write as" as a regex substitution
                if (dict.Any(d => string.Equals((string.IsNullOrWhiteSpace(d.From) ? d.To : d.From).Trim(), c.from, StringComparison.OrdinalIgnoreCase))) continue;
                var entry = new DictEntry { From = c.from, To = c.to };
                var rx = new Regex($@"(?<![\w]){Regex.Escape(c.from)}(?![\w])", RegexOptions.IgnoreCase);
                if (refs.Any(w => rx.Replace(w, c.to.Replace("$", "$$")) != w)) continue;    // would damage a correct sentence
                var affected = results.Where(r => !r.NoSound && rx.IsMatch(r.Clean)).ToList();
                if (affected.Count == 0) continue;
                var trial = dict.Append(entry).ToList();
                int delta = 0, worse = 0; var fixedAudio = new HashSet<string>(); var fixedSpeakers = new HashSet<string>(); var updates = new List<(Result r, string clean, int err, int errCs)>();
                foreach (var r in affected)
                {
                    var cl = TextCleaner.Clean(r.Raw, settings, trial, snippets);
                    var (_, e, ecs) = Score(r.Written, cl);
                    int dlt = ecs - r.ErrCs; delta += dlt;
                    if (dlt < 0) { fixedAudio.Add(AudioOf(r)); fixedSpeakers.Add(SpeakerOf(r)); } else if (dlt > 0) worse++;
                    updates.Add((r, cl, e, ecs));
                }
                // keep it only if it is systematic: different recordings AND different speakers, nothing made worse
                int better = fixedAudio.Count;
                if (worse > 0 || better < 2 || fixedSpeakers.Count < 2 || delta >= 0) continue;
                // Swapping one ordinary word for another ("a hotel" -> "the hotel") is only right in some sentences: list it, don't apply it.
                if (IsWordSwap(c.from, c.to, refLower, refCased)) { review.Add((c.from, c.to, better)); continue; }
                dict.Add(entry); acceptedThisPass++;
                var ex = updates.First(u => u.errCs < u.r.ErrCs);
                learned.Add((c.from, c.to, better, -delta, Change(ex.r.Clean, ex.clean)));
                foreach (var u in updates) { u.r.Clean = u.clean; u.r.Err = u.err; u.r.ErrCs = u.errCs; }
            }
            if (acceptedThisPass == 0) break;
        }

        // ---- outputs ----
        var suggested = dict.Select(d => new DictEntry { From = d.From, To = d.To }).ToList();
        File.WriteAllText(Path.Combine(outDir, "dictionary.suggested.json"), JsonSerializer.Serialize(suggested, new JsonSerializerOptions { WriteIndented = true, Encoder = Json.Encoder }));
        var tsv = new StringBuilder("heard as\twrite as\trecordings fixed\twords fixed\texample\n");
        foreach (var l in learned) tsv.Append($"{l.from}\t{l.to}\t{l.fixedIn}\t{l.wordsFixed}\t{l.example}\n");
        File.WriteAllText(Path.Combine(outDir, "learned-corrections.tsv"), tsv.ToString());
        var csv = new StringBuilder("kind,condition,mode,voice,id,category,ref_words,errors_before,errors_after,audio_s,ms,peak,no_sound,written,heard\n");
        foreach (var r in results)
            csv.Append($"{r.Kind},{r.Condition},{r.Mode},{Csv(r.Voice)},{r.Id},{r.Category},{r.RefWords},{r.BaseErr},{r.Err},{r.AudioSec},{r.Ms},{r.Peak:0.0000},{r.NoSound},{Csv(r.Written)},{Csv(r.Clean)}\n");
        File.WriteAllText(Path.Combine(outDir, "results.csv"), csv.ToString());
        File.WriteAllText(Path.Combine(outDir, "report.md"), Report(outDir, results, learned, existing.Count, review));
    }

    /// <summary>
    /// True when "heard as" is made only of words that also appear correctly in the test text (so the replacement
    /// would also fire on correct speech), or is a capitalization change of an ordinary word.
    /// Safe corrections contain at least one token that only a recognition mistake produces ("MOSFITs", "boat plot", "bomb").
    /// </summary>
    /// <summary>Everyday English words: a "heard as" made only of these would also fire on correct speech.</summary>
    static readonly HashSet<string> Common = new((
        "a about above after again against all almost also always am an and another any anything are around as ask at away back be because been before being below best better between big both but by call came can come could day did different do does done down dry during each early end enough even every far feel few find first fit fits for found from full get give go going good got great had has have having he her here him his home house how i if in into is it its just keep kind know last late leave left less let life like line little long look lot made make man many may me mean might more most move much must my need never new next no not nothing now number of off often old on once one only open or other our out over own part people place put rather read reading real really right room run said same saw say see seem set she should show side since small so some something sometimes soon start still such sure take tell than that the their them then there these they thing things think this those though thought three through time to today together told too took top total try turn two under until up us use used very want was water way we well went were what when where which while who whole why will with without word work world would write year yes yet you young your " +
        "hotel car book trip dinner family church school week weekend month morning night today tomorrow yesterday table door phone email meeting call note list plan team test board part order").Split(' ', StringSplitOptions.RemoveEmptyEntries));

    static bool IsWordSwap(string from, string to, HashSet<string> refLower, HashSet<string> refCased)
    {
        var f = Tokenize(from); var t = Tokenize(to);
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase) && f.Count == t.Count)
            return f.Where((tok, i) => tok.Cased != t[i].Cased).Any(tok => refCased.Contains(tok.Cased));
        return f.All(tok => refLower.Contains(tok.Norm) || Common.Contains(tok.Norm));
    }

    // ---------- mining: where does the output differ from what should have been typed? ----------
    static Dictionary<string, (string from, string to, int support)> Mine(List<Result> results)
    {
        var map = new Dictionary<string, (string from, string to, HashSet<string> who)>();
        foreach (var r in results)
        {
            if (r.ErrCs == 0 || r.NoSound) continue;
            var R = Tokenize(r.Written); var H = Tokenize(r.Clean);
            var ops = Align(R, H);
            int i = 0;
            while (i < ops.Count)
            {
                if (ops[i].op == Op.Eq) { i++; continue; }
                int j = i; while (j < ops.Count && ops[j].op != Op.Eq) j++;
                // region ops[i..j): ref tokens and hyp tokens involved
                var rIdx = ops.Skip(i).Take(j - i).Where(o => o.r >= 0 && o.op != Op.Ins).Select(o => o.r).ToList();
                var hIdx = ops.Skip(i).Take(j - i).Where(o => o.h >= 0 && o.op != Op.Del).Select(o => o.h).ToList();
                int prevR = i > 0 ? ops[i - 1].r : -1, prevH = i > 0 ? ops[i - 1].h : -1;
                int nextR = j < ops.Count ? ops[j].r : -1, nextH = j < ops.Count ? ops[j].h : -1;
                void Add(int r0, int r1, int h0, int h1)
                {
                    if (r0 < 0 || h0 < 0 || r1 < r0 || h1 < h0 || r1 - r0 > 5 || h1 - h0 > 5) return;
                    if (R.Skip(r0).Take(r1 - r0 + 1).Any(t => t.Norm[0] == '¶') || H.Skip(h0).Take(h1 - h0 + 1).Any(t => t.Norm[0] == '¶')) return;
                    var from = r.Clean[H[h0].Start..H[h1].End].Trim(); var to = r.Written[R[r0].Start..R[r1].End].Trim();
                    if (from.Length < 2 || to.Length == 0 || from == to || from.Contains('\n') || to.Contains('\n')) return;
                    if (h0 == h1 && Stop.Contains(H[h0].Norm)) return;          // a lone common word is too risky to replace
                    var key = from.ToLowerInvariant() + "\u0001" + to;
                    if (!map.TryGetValue(key, out var v)) map[key] = v = (from, to, new HashSet<string>());
                    v.who.Add(AudioOf(r));
                }
                bool hasR = rIdx.Count > 0, hasH = hIdx.Count > 0;
                int R0 = hasR ? rIdx.Min() : -1, R1 = hasR ? rIdx.Max() : -1, H0 = hasH ? hIdx.Min() : -1, H1 = hasH ? hIdx.Max() : -1;
                if (hasR && hasH) Add(R0, R1, H0, H1);
                // with one word of context on either side (lets Vox fix merges, splits and dropped words safely)
                if (prevR >= 0 && prevH >= 0) Add(prevR, hasR ? R1 : prevR, prevH, hasH ? H1 : prevH);
                if (nextR >= 0 && nextH >= 0) Add(hasR ? R0 : nextR, nextR, hasH ? H0 : nextH, nextH);
                i = j;
            }
        }
        return map.ToDictionary(kv => kv.Key, kv => (kv.Value.from, kv.Value.to, kv.Value.who.Count));
    }

    static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "a","an","the","and","or","but","to","of","in","on","at","for","is","it","its","it's","be","as","by","we","i","you","he","she","they",
        "this","that","so","if","with","from","are","was","were","not","no","do","can","will","have","has","had","me","my","our","your","up","out",
    };

    // ---------- scoring ----------
    sealed record Tok(string Norm, string Cased, int Start, int End);
    enum Op { Eq, Case, Sub, Ins, Del }

    /// <summary>(reference words, word errors ignoring case, word errors counting capitalization)</summary>
    static (int refWords, int err, int errCs) Score(string written, string heard)
    {
        var R = Tokenize(written); var H = Tokenize(heard);
        var ops = Align(R, H);
        int err = ops.Count(o => o.op is Op.Sub or Op.Ins or Op.Del);
        return (R.Count, err, err + ops.Count(o => o.op == Op.Case));
    }

    static readonly Regex TokRx = new(@"\n+|[^\s\-–—/]+", RegexOptions.Compiled);
    static List<Tok> Tokenize(string text)
    {
        var list = new List<Tok>();
        foreach (Match m in TokRx.Matches(text.Replace("\r\n", "\n")))
        {
            if (m.Value[0] == '\n') { list.Add(new Tok("¶" + m.Length, "¶", m.Index, m.Index + m.Length)); continue; }
            int s = 0, e = m.Value.Length;
            while (s < e && IsEdgePunct(m.Value[s])) s++;
            while (e > s && IsEdgePunct(m.Value[e - 1])) e--;
            if (s >= e) continue;
            var cased = m.Value[s..e];
            cased = Regex.Replace(cased, @"(?<=\d),(?=\d{3})", "");          // 1,250 = 1250
            cased = Regex.Replace(cased, @"(?<=\p{L})\.(?=\p{L})", "");      // a.m = am
            // "70%" = "70 percent", "eight" = "8": either way of writing a number counts as right
            bool pct = cased.Length > 1 && cased.EndsWith('%');
            var core = pct ? cased[..^1] : cased;
            var low = core.ToLowerInvariant();
            list.Add(new Tok(NumberWords.TryGetValue(low, out var dig) ? dig : low, core, m.Index + s, m.Index + e - (pct ? 1 : 0)));
            if (pct) list.Add(new Tok("percent", "percent", m.Index + e - 1, m.Index + e));
        }
        return list;
    }
    static readonly Dictionary<string, string> NumberWords = new[] { "zero","one","two","three","four","five","six","seven","eight","nine","ten","eleven","twelve",
        "thirteen","fourteen","fifteen","sixteen","seventeen","eighteen","nineteen" }.Select((w, i) => (w, i.ToString()))
        .Concat(new[] { ("twenty","20"),("thirty","30"),("forty","40"),("fifty","50"),("sixty","60"),("seventy","70"),("eighty","80"),("ninety","90"),("hundred","100") })
        .ToDictionary(p => p.Item1, p => p.Item2);
    static bool IsEdgePunct(char c) => (char.IsPunctuation(c) || c == '"') && c != '%';

    static List<(Op op, int r, int h)> Align(List<Tok> R, List<Tok> H)
    {
        int n = R.Count, m = H.Count; var d = new int[n + 1, m + 1];
        for (int i = 0; i <= n; i++) d[i, 0] = i;
        for (int j = 0; j <= m; j++) d[0, j] = j;
        for (int i = 1; i <= n; i++)
            for (int j = 1; j <= m; j++)
                d[i, j] = Math.Min(d[i - 1, j - 1] + (R[i - 1].Norm == H[j - 1].Norm ? 0 : 1), Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1));
        var ops = new List<(Op, int, int)>();
        int a = n, b = m;
        while (a > 0 || b > 0)
        {
            if (a > 0 && b > 0 && d[a, b] == d[a - 1, b - 1] + (R[a - 1].Norm == H[b - 1].Norm ? 0 : 1))
            {
                var op = R[a - 1].Norm != H[b - 1].Norm ? Op.Sub : string.Equals(R[a - 1].Cased, H[b - 1].Cased, StringComparison.OrdinalIgnoreCase) && R[a - 1].Cased != H[b - 1].Cased ? Op.Case : Op.Eq;
                ops.Add((op, a - 1, b - 1)); a--; b--;
            }
            else if (a > 0 && d[a, b] == d[a - 1, b] + 1) { ops.Add((Op.Del, a - 1, -1)); a--; }
            else { ops.Add((Op.Ins, -1, b - 1)); b--; }
        }
        ops.Reverse();
        return ops;
    }

    // ---------- report ----------
    static string Report(string outDir, List<Result> all, List<(string from, string to, int fixedIn, int wordsFixed, string example)> learned, int existingCount, List<(string from, string to, int support)> review)
    {
        var sb = new StringBuilder();
        string Pct(int e, int w) => w == 0 ? "-" : (100.0 * e / w).ToString("0.0", CultureInfo.InvariantCulture) + "%";
        string Acc(IEnumerable<Result> rs, bool after) { var l = rs.ToList(); int w = l.Sum(r => r.RefWords); int e = l.Sum(r => after ? r.Err : r.BaseErr); return w == 0 ? "-" : (100 - 100.0 * e / w).ToString("0.0", CultureInfo.InvariantCulture) + "%"; }
        string Perfect(IEnumerable<Result> rs) { var l = rs.ToList(); return l.Count == 0 ? "-" : $"{100.0 * l.Count(r => r.ErrCs == 0) / l.Count:0}%"; }

        var syn = all.Where(r => r.Kind != "mic").ToList(); var mic = all.Where(r => r.Kind == "mic").ToList();
        sb.AppendLine("# Vox dictation test report");
        sb.AppendLine();
        sb.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm}. Model: {all.Select(r => r.Key.Split('|')[0]).Distinct().FirstOrDefault()}. " +
                      $"{all.Count} transcriptions, {all.Sum(r => r.AudioSec) / 60:0} min of audio, {all.Select(r => r.Voice.Split(' ')[0]).Distinct().Count()} synthetic speakers, nobody talking.");
        sb.AppendLine();
        sb.AppendLine("**Word accuracy** = share of words right, ignoring capitalization and punctuation. **Perfect** = whole dictation exactly right including capitals.");
        sb.AppendLine("*Before* uses your current dictionary; *after* adds the corrections Vox learned in this run.");
        sb.AppendLine();
        sb.AppendLine("| Test | Transcriptions | Word accuracy before | after | Perfect after |");
        sb.AppendLine("|---|---:|---:|---:|---:|");
        sb.AppendLine($"| Synthetic, straight into the engine | {syn.Count} | {Acc(syn, false)} | {Acc(syn, true)} | {Perfect(syn)} |");
        if (mic.Count > 0) sb.AppendLine($"| Played out loud into the real mic (live mode) | {mic.Count(r => r.Mode == "live")} | {Acc(mic.Where(r => r.Mode == "live"), false)} | {Acc(mic.Where(r => r.Mode == "live"), true)} | {Perfect(mic.Where(r => r.Mode == "live"))} |");
        sb.AppendLine();

        void Table(string title, string col, IEnumerable<IGrouping<string, Result>> groups, Func<string, string>? desc = null)
        {
            sb.AppendLine($"## {title}"); sb.AppendLine();
            sb.AppendLine($"| {col} | n | Word accuracy before | after | Perfect after |" + (desc != null ? " What it imitates |" : ""));
            sb.AppendLine("|---|---:|---:|---:|---:|" + (desc != null ? "---|" : ""));
            foreach (var g in groups.OrderByDescending(g => g.Sum(r => r.RefWords) == 0 ? 0 : 1.0 * g.Sum(r => r.Err) / g.Sum(r => r.RefWords)).Reverse())
                sb.AppendLine($"| {g.Key} | {g.Count()} | {Acc(g, false)} | {Acc(g, true)} | {Perfect(g)} |" + (desc != null ? $" {desc(g.Key)} |" : ""));
            sb.AppendLine();
        }
        Table("By volume level and noisy place (synthetic)", "Condition", syn.Where(r => r.Mode == "whole").GroupBy(r => r.Condition),
              k => TrainAudio.Describe.TryGetValue(k, out var d) ? d : "");
        Table("By kind of speech", "Category", all.Where(r => r.Mode == "whole").GroupBy(r => r.Category));
        Table("By speaker", "Voice", syn.Where(r => r.Mode == "whole").GroupBy(r => r.Voice.Split(' ')[0]));

        var paraLive = syn.Where(r => r.Kind == "paragraph").ToList();
        if (paraLive.Count > 0)
        {
            sb.AppendLine("## Live mode vs whole clip (long passages)"); sb.AppendLine();
            sb.AppendLine("Live mode sends phrases to the engine while you are still talking. If it ever drops audio, its accuracy falls below whole-clip.");
            sb.AppendLine();
            sb.AppendLine("| Condition | Whole clip | Live | Avg engine time, live |"); sb.AppendLine("|---|---:|---:|---:|");
            foreach (var g in paraLive.GroupBy(r => r.Condition))
                sb.AppendLine($"| {g.Key} | {Acc(g.Where(r => r.Mode == "whole"), true)} | {Acc(g.Where(r => r.Mode == "live"), true)} | {g.Where(r => r.Mode == "live").Average(r => r.Ms) / 1000:0.0} s |");
            sb.AppendLine();
        }

        if (mic.Count > 0)
        {
            var calPath = Path.Combine(outDir, "mic-calibration.md");
            sb.AppendLine("## Played out loud into the real microphone"); sb.AppendLine();
            if (File.Exists(calPath)) { sb.AppendLine(File.ReadAllText(calPath)); }
            sb.AppendLine("| Playback | n | Live | Whole clip | \"No sound heard\" | Wait after the clip ended (live) |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|");
            foreach (var g in mic.GroupBy(r => r.Condition).OrderBy(g => g.Key))
            {
                var lv = g.Where(r => r.Mode == "live").ToList();
                sb.AppendLine($"| {g.Key.Replace("air-", "laptop speakers, ").Replace("track-", "other device, ")} | {lv.Count} | {Acc(lv, true)} | {Acc(g.Where(r => r.Mode == "whole"), true)} | {lv.Count(r => r.NoSound)} | {(lv.Count > 0 ? lv.Average(r => r.Ms) : 0):0} ms |");
            }
            sb.AppendLine();
            sb.AppendLine("The recordings are kept in `mic\\` so later Vox changes can be re-tested on exactly the same audio.");
            sb.AppendLine();
        }

        sb.AppendLine($"## What Vox learned ({learned.Count} corrections)"); sb.AppendLine();
        if (learned.Count == 0) sb.AppendLine("No correction passed the safety checks.");
        else
        {
            sb.AppendLine("Each one fixed at least two recordings, made none worse, and changes no correct sentence in the test text. Review them before applying; drop any that look wrong for your normal writing.");
            sb.AppendLine();
            sb.AppendLine("| Heard as | Write as | Recordings fixed | Example |"); sb.AppendLine("|---|---|---:|---|");
            foreach (var l in learned.OrderByDescending(l => l.wordsFixed)) sb.AppendLine($"| {Md(l.from)} | {Md(l.to)} | {l.fixedIn} | {Md(l.example)} |");
        }
        sb.AppendLine();
        sb.AppendLine($"Saved as `dictionary.suggested.json` ({existingCount} existing entries + {learned.Count} learned).");
        sb.AppendLine();
        if (review.Count > 0)
        {
            sb.AppendLine($"## Held back for you to decide ({review.Count})"); sb.AppendLine();
            sb.AppendLine("These helped in the test but swap one ordinary word for another, so they would also change correct speech. They are **not** in the suggested dictionary. Add any you want by hand in Vox > Dictionary.");
            sb.AppendLine();
            sb.AppendLine("| Heard as | Write as | Recordings fixed |"); sb.AppendLine("|---|---|---:|");
            foreach (var v in review.OrderByDescending(v => v.support)) sb.AppendLine($"| {Md(v.from)} | {Md(v.to)} | {v.support} |");
            sb.AppendLine();
        }

        // what is still wrong and can't be fixed by the dictionary
        var residual = Mine(all).Values.Where(v => v.support >= 3).OrderByDescending(v => v.support).Take(25).ToList();
        sb.AppendLine("## Most common remaining mistakes"); sb.AppendLine();
        sb.AppendLine("Not fixed automatically, because a simple replacement would break other text or the mistake changes from take to take.");
        sb.AppendLine();
        sb.AppendLine("| Vox typed | Should be | Times |"); sb.AppendLine("|---|---|---:|");
        foreach (var v in residual) sb.AppendLine($"| {Md(v.from)} | {Md(v.to)} | {v.support} |");
        sb.AppendLine();

        sb.AppendLine("## Hardest dictations (after learning)"); sb.AppendLine();
        foreach (var r in all.Where(r => r.Err > 0 && (r.Mode == "whole" || r.Kind == "mic" && r.Mode == "live")).OrderByDescending(r => r.RefWords == 0 ? 0 : 1.0 * r.Err / r.RefWords).Take(15))
        {
            sb.AppendLine($"- **{r.Condition}**, {r.Voice}, {Pct(r.Err, r.RefWords)} words wrong" + (r.NoSound ? " (Vox would say \"No sound heard\")" : ""));
            sb.AppendLine($"  - Should be: {Md(Trim(r.Written, 220))}");
            sb.AppendLine($"  - Vox typed: {Md(Trim(r.Clean, 220))}");
        }
        sb.AppendLine();
        sb.AppendLine("## Limits of this test"); sb.AppendLine();
        sb.AppendLine("- The voices are Windows text-to-speech. They are clearer and more regular than a real person, so real accuracy will be somewhat lower; the ranking of conditions is the useful part.");
        sb.AppendLine("- Noise, echo and volume are simulated in the synthetic test. The played-out-loud test uses a real room and the real microphone path (recorder, level check, live transcriber).");
        sb.AppendLine("- The speech model itself is not retrained. Learning happens in Vox's dictionary (applied after recognition), which works for both Parakeet and Whisper.");
        return sb.ToString();
    }

    // ---------- small helpers ----------
    static string AudioOf(Result r) => r.Key[..r.Key.LastIndexOf('|')];      // live and whole runs of one recording count once
    static string SpeakerOf(Result r) => r.Voice.Split(' ')[0];

    /// <summary>"...before..." -> "...after...", centred on the first difference.</summary>
    static string Change(string a, string b)
    {
        int i = 0; while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        int s = Math.Max(0, i - 45); s = s == 0 ? 0 : Math.Max(0, a.LastIndexOf(' ', s) + 1);
        string Cut(string t) { var e = Math.Min(t.Length, i + 45); var piece = t[s..e]; return (s > 0 ? "..." : "") + piece.Replace("\n", " / ") + (e < t.Length ? "..." : ""); }
        return $"\"{Cut(a)}\" -> \"{Cut(b)}\"";
    }

    static List<Clip> ReadManifest(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        return File.ReadAllLines(path, Encoding.UTF8).Skip(1).Where(l => l.Trim().Length > 0).Select(l => l.Split('\t'))
            .Select(p => new Clip(p[0], p[1], p[2], p[3], Path.GetFullPath(Path.Combine(dir, p[4])), p[5].Replace("\\n", "\n"))).ToList();
    }

    static List<Result> ReadResults(string path)
    {
        var list = new List<Result>();
        if (!File.Exists(path)) return list;
        foreach (var l in File.ReadLines(path)) { if (l.Trim().Length == 0) continue; try { var r = JsonSerializer.Deserialize<Result>(l); if (r != null) list.Add(r); } catch { } }
        return list.GroupBy(r => r.Key).Select(g => g.Last()).ToList();
    }

    /// <summary>The user's dictionary and snippets, read-only (never written by the trainer).</summary>
    static (List<DictEntry> dict, List<Snippet> snippets) LoadUserData(AppSettings s)
    {
        List<T> Read<T>(string name)
        {
            try { var p = Path.Combine(s.DataFolder, name); return File.Exists(p) ? JsonSerializer.Deserialize<List<T>>(File.ReadAllText(p)) ?? new() : new(); }
            catch { return new(); }
        }
        // test tools only: VOX_DICT=path measures a candidate dictionary without touching the real one
        var alt = Environment.GetEnvironmentVariable("VOX_DICT");
        if (!string.IsNullOrEmpty(alt) && File.Exists(alt))
            return (JsonSerializer.Deserialize<List<DictEntry>>(File.ReadAllText(alt)) ?? new(), Read<Snippet>("snippets.json"));
        return (Read<DictEntry>("dictionary.json"), Read<Snippet>("snippets.json"));
    }

    /// <summary>A fixed set of other speech clips used as background voices.</summary>
    static Func<int, float[]> BackgroundPool(List<Clip> clips)
    {
        var pick = clips.Where(c => c.Kind == "sentence").Where((_, i) => i % 23 == 5).Take(24).Select(c => TrainAudio.Load(c.Wav)).ToList();
        return seed => pick.Count == 0 ? Array.Empty<float>() : pick[(seed & int.MaxValue) % pick.Count];
    }

    static float[] Scale(float[] x, float peak) { float p = TrainAudio.Peak(x); var y = new float[x.Length]; float g = p > 0 ? peak / p : 0; for (int i = 0; i < x.Length; i++) y[i] = x[i] * g; return y; }
    static float[] Pad(float[] x) { var y = new float[x.Length + (int)(1.1 * TrainAudio.Rate)]; Array.Copy(x, 0, y, (int)(0.6 * TrainAudio.Rate), x.Length); return y; }
    static float Rms(float[] x) { double q = 0; foreach (var v in x) q += v * v; return x.Length == 0 ? 0 : (float)Math.Sqrt(q / x.Length); }
    static int Hash(string s) { unchecked { int h = (int)2166136261; foreach (char c in s) h = (h ^ c) * 16777619; return h & int.MaxValue; } }
    static string Trim(string s, int max = 120) => s.Length <= max ? s.Replace("\n", " / ") : s[..max].Replace("\n", " / ") + "...";
    static string Md(string s) => s.Replace("|", "\\|").Replace("\n", " / ");
    static string Csv(string s) => "\"" + s.Replace("\"", "\"\"").Replace("\n", "\\n") + "\"";
}
