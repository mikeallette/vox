using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Vox.Core;

/// <summary>
/// One playback device recorded through WASAPI loopback (what Teams/Zoom/Meet plays on that device).
/// Keeps the device's audio on the meeting's clock: Windows sends no data at all while nothing plays on a device,
/// so gaps are filled with silence; otherwise "Others" timestamps would drift behind "You".
/// </summary>
internal sealed class PlaybackSource : IDisposable
{
    public readonly int Index;                 // 1, 2, ... (0 is the microphone)
    public readonly string DeviceId, Name;
    public readonly double OffsetSec;          // meeting time when this device's recording started
    public string? WavPath { get; }
    public bool HeardSound { get; private set; }
    public bool Dead { get; private set; }

    readonly Stopwatch _clock;
    readonly WasapiLoopbackCapture? _cap;
    readonly Resampler16k? _rs;
    readonly Segmenter _seg;
    WaveFileWriter? _wav;
    readonly object _gate = new();
    long _written;                             // 16 kHz samples so far, including filled-in silence
    double _lastPacketSec;

    public PlaybackSource(int index, MMDevice? device, Stopwatch clock, Action<AudioSegment> emit, string? wavPath, double? offsetSec = null)
    {
        Index = index; _clock = clock; OffsetSec = offsetSec ?? clock.Elapsed.TotalSeconds; _lastPacketSec = OffsetSec;
        DeviceId = device?.ID ?? "test-" + index; Name = device?.FriendlyName ?? "test input " + index;
        _seg = new Segmenter("Others", OffsetSec, emit, source: index);
        if (wavPath != null) { WavPath = wavPath; _wav = new WaveFileWriter(wavPath, new WaveFormat(16000, 16, 1)); }
        if (device != null)
        {
            _cap = new WasapiLoopbackCapture(device);
            _rs = new Resampler16k(_cap.WaveFormat.SampleRate);
            _cap.DataAvailable += OnData;
            _cap.RecordingStopped += (_, e) => { if (e.Exception != null) { Dead = true; Log.Error("loopback stopped: " + Name, e.Exception); } };
            _cap.StartRecording();
        }
    }

    double NowSec => _clock.Elapsed.TotalSeconds;
    long NowSample => (long)((NowSec - OffsetSec) * 16000);

    void OnData(object? s, WaveInEventArgs e)
    {
        var wf = _cap!.WaveFormat;
        int ch = wf.Channels, bytesPer = wf.BitsPerSample / 8;
        int frames = e.BytesRecorded / (bytesPer * ch);
        if (frames == 0) return;
        var mono = new float[frames];
        bool isFloat = wf.Encoding == WaveFormatEncoding.IeeeFloat || (wf.Encoding == WaveFormatEncoding.Extensible && wf.BitsPerSample == 32);
        for (int i = 0; i < frames; i++)
        {
            float acc = 0;
            for (int c = 0; c < ch; c++)
            {
                int off = (i * ch + c) * bytesPer;
                acc += isFloat ? BitConverter.ToSingle(e.Buffer, off) : bytesPer == 2 ? BitConverter.ToInt16(e.Buffer, off) / 32768f : 0f;
            }
            mono[i] = acc / ch;
        }
        var outp = new List<float>(frames / 2);
        lock (_gate)
        {
            _rs!.Process(mono, frames, outp);
            // Sound resumed after a quiet spell: fill the quiet time first. During continuous playback nothing is
            // inserted, so small clock differences never put a gap inside a word.
            if (NowSec - _lastPacketSec > 0.2) PadTo(NowSample - outp.Count);
            _lastPacketSec = NowSec;
            Write(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(outp));
        }
    }

    /// <summary>Called a few times a second: while the device is silent, keep its timeline (and phrase cutting) moving.</summary>
    public void Tick()
    {
        if (_cap == null || Dead) return;
        lock (_gate) { if (NowSec - _lastPacketSec > 0.4) PadTo(NowSample); }
    }

    void PadTo(long target)
    {
        long gap = target - _written;
        if (gap < 1600) return;                        // under 0.1 s: normal buffering
        var z = new float[(int)Math.Min(gap, 16000)];
        while (gap > 0) { int n = (int)Math.Min(gap, z.Length); Write(z.AsSpan(0, n)); gap -= n; }
    }

    void Write(ReadOnlySpan<float> x)
    {
        if (x.Length == 0) return;
        _seg.Push(x);
        if (_wav != null)
        {
            var buf = new short[x.Length];
            for (int i = 0; i < x.Length; i++)
            {
                float v = x[i];
                if (!HeardSound && Math.Abs(v) > 0.003f) HeardSound = true;
                buf[i] = (short)Math.Clamp(v * 32767f, -32768f, 32767f);
            }
            _wav.WriteSamples(buf, 0, buf.Length);
        }
        else if (!HeardSound) foreach (var v in x) if (Math.Abs(v) > 0.003f) { HeardSound = true; break; }
        _written += x.Length;
    }

    /// <summary>Test input: feeds a whole recording as if it had played on this device.</summary>
    public void PushAll(float[] samples) { lock (_gate) Write(samples); }

    /// <summary>End of meeting: fill silence up to now, send the last phrase, close the audio file.</summary>
    public string Debug => $"written {_written / 16000.0:0.00} s, now {NowSample / 16000.0:0.00} s, last packet {_lastPacketSec:0.00}";

    public void Finish()
    {
        try { _cap?.StopRecording(); } catch { }
        lock (_gate)
        {
            Log.Info($"loopback finish {Name}: {Debug}");
            if (_cap != null && !Dead) PadTo(NowSample);
            _seg.Flush();
            _wav?.Dispose(); _wav = null;
        }
    }

    public void Dispose() { try { _cap?.Dispose(); } catch { } lock (_gate) { _wav?.Dispose(); _wav = null; } }
}

/// <summary>
/// Records a meeting entirely on this PC: microphone ("You") plus everything the PC plays ("Others", WASAPI loopback of
/// every active playback device, so it does not matter whether Teams/Zoom/Meet uses the speakers or a headset).
/// Transcribes locally while it runs and writes a Markdown transcript (.md) into the Meetings folder. When the speaker
/// models are installed, "Others" lines are labeled Speaker 1, Speaker 2, ... after the meeting (see SpeakerDiarizer).
/// </summary>
public sealed class MeetingRecorder
{
    sealed record Entry(double StartSec, double EndSec, string Speaker, string Text, int Source);

    readonly AppSettings _settings;
    readonly DataStore _data;
    readonly SpeechEngine _engine;

    WaveInEvent? _mic;
    Segmenter? _micSeg;
    readonly List<PlaybackSource> _sources = new();
    readonly object _srcGate = new();
    Timer? _tick;
    int _tickCount;
    BlockingCollection<AudioSegment> _queue = new();
    Thread? _worker;
    readonly List<Entry> _entries = new();
    readonly object _gate = new();
    Stopwatch _clock = new();
    DateTime _startedAt;
    string _title = "Meeting";
    string _workDir = "";
    string _speakerNote = "";

    public bool IsRunning { get; private set; }
    public string? FilePath { get; private set; }
    public TimeSpan Elapsed => _clock.Elapsed;
    public int Pending => _queue.Count;
    public int LineCount { get { lock (_gate) return _entries.Count; } }
    public event Action<string>? Status;
    public event Action<string, string>? LineAdded;    // speaker, text
    /// <summary>The lines of the last saved transcript (for the meeting test).</summary>
    internal List<(double Start, double End, string Speaker, string Text, int Source)> FinalLines { get; private set; } = new();

    public MeetingRecorder(AppSettings s, DataStore d, SpeechEngine e) { _settings = s; _data = d; _engine = e; }

    /// <summary>Playback devices for the Settings picker: (id, name). "" = all devices, "default" = Windows defaults only.</summary>
    public static List<(string Id, string Name)> PlaybackDevices()
    {
        var list = new List<(string, string)>();
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)) list.Add((d.ID, d.FriendlyName));
        }
        catch (Exception ex) { Log.Error("list playback devices", ex); }
        return list;
    }

    void Begin(string? title)
    {
        if (!_engine.IsLoaded) throw new InvalidOperationException("No speech model loaded. Open Settings and choose one.");
        _title = string.IsNullOrWhiteSpace(title) ? "Meeting" : title.Trim();
        _startedAt = DateTime.Now;
        Directory.CreateDirectory(_data.MeetingsDir);
        var safe = string.Concat(_title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        FilePath = Path.Combine(_data.MeetingsDir, $"{_startedAt:yyyy-MM-dd HH-mm} {safe}.md");
        // meeting audio for speaker labeling lives here only until the transcript is written
        _workDir = Path.Combine(_data.MeetingsDir, ".recording");
        Directory.CreateDirectory(_workDir);
        try { File.SetAttributes(_workDir, FileAttributes.Directory | FileAttributes.Hidden); } catch { }
        lock (_gate) _entries.Clear();
        lock (_srcGate) { foreach (var s in _sources) s.Dispose(); _sources.Clear(); }
        _speakerNote = "";
        _queue = new BlockingCollection<AudioSegment>();
        _clock = Stopwatch.StartNew();
    }

    public void Start(string? title = null)
    {
        if (IsRunning) return;
        Begin(title);

        // microphone
        _micSeg = new Segmenter("You", 0, _queue.Add, source: 0);
        _mic = new WaveInEvent { DeviceNumber = MicRecorder.ResolveDevice(_settings.MicrophoneName), WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 60 };
        _mic.DataAvailable += (_, e) =>
        {
            int n = e.BytesRecorded / 2; var f = new float[n];
            for (int i = 0; i < n; i++) f[i] = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
            _micSeg!.Push(f);
        };
        _mic.StartRecording();

        // everything the PC plays
        if (_settings.MeetingIncludeSystemAudio)
        {
            AddPlaybackDevices();
            lock (_srcGate) if (_sources.Count == 0) Status?.Invoke("System audio unavailable; recording microphone only.");
            _tick = new Timer(_ => OnTick(), null, 250, 250);
        }

        WriteHeader();
        IsRunning = true;
        StartWorker();
        Status?.Invoke("Recording");
    }

    void StartWorker()
    {
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "Vox.MeetingTranscriber" };
        _worker.Start();
    }

    List<MMDevice> WantedDevices(MMDeviceEnumerator en)
    {
        var mode = _settings.MeetingPlayback ?? "";
        var all = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        if (mode == "") return all;
        if (mode == "default")
        {
            var ids = new List<string>();
            foreach (var role in new[] { Role.Multimedia, Role.Communications })
                try { ids.Add(en.GetDefaultAudioEndpoint(DataFlow.Render, role).ID); } catch { }
            return all.Where(d => ids.Contains(d.ID)).ToList();
        }
        var pick = all.Where(d => d.ID == mode).ToList();
        return pick.Count > 0 ? pick : all;   // chosen device unplugged: fall back to everything rather than record nothing
    }

    void AddPlaybackDevices()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var d in WantedDevices(en))
            {
                lock (_srcGate)
                {
                    if (_sources.Any(s => s.DeviceId == d.ID && !s.Dead)) continue;
                    int idx = _sources.Count + 1;
                    try
                    {
                        var src = new PlaybackSource(idx, d, _clock, _queue.Add, Path.Combine(_workDir, $"{_startedAt:yyyyMMdd-HHmmss}-{idx}.wav"));
                        _sources.Add(src);
                        Log.Info($"meeting: recording playback device {idx}: {d.FriendlyName}");
                    }
                    catch (Exception ex) { Log.Error("loopback start " + d.FriendlyName, ex); }
                }
            }
        }
        catch (Exception ex) { Log.Error("loopback devices", ex); }
    }

    void OnTick()
    {
        if (!IsRunning) return;
        PlaybackSource[] srcs; lock (_srcGate) srcs = _sources.ToArray();
        foreach (var s in srcs) try { s.Tick(); } catch (Exception ex) { Log.Error("loopback tick", ex); }
        // a headset plugged in mid-meeting: start recording it too (every 3 s)
        if (++_tickCount % 12 == 0 && (_settings.MeetingPlayback ?? "") != "default") AddPlaybackDevices();
    }

    void WorkerLoop()
    {
        var cleanSettings = new AppSettings { RemoveFillers = true, VoiceCommands = false };
        var prompt = TextCleaner.BuildPrompt(_data.Dictionary);
        foreach (var seg in _queue.GetConsumingEnumerable())
        {
            try
            {
                var raw = _engine.TranscribeAsync(AudioPrep.Condition(seg.Samples), _settings.Language, _settings.Threads, prompt).GetAwaiter().GetResult();
                var text = TextCleaner.Clean(raw, cleanSettings, _data.Dictionary, Array.Empty<Snippet>());
                if (text.Length < 2) continue;
                lock (_gate) _entries.Add(new Entry(seg.StartSec, seg.StartSec + seg.Samples.Length / 16000.0, seg.Speaker, text, seg.Source));
                AppendLine(seg.StartSec, seg.Speaker, text);
                LineAdded?.Invoke(seg.Speaker, text);
            }
            catch (Exception ex) { Log.Error("meeting transcribe", ex); }
        }
    }

    public async Task<string?> StopAsync()
    {
        if (!IsRunning) return FilePath;
        IsRunning = false;
        Status?.Invoke("Finishing transcript");
        _tick?.Dispose(); _tick = null;
        try { _mic?.StopRecording(); } catch { }
        await Task.Delay(300);
        _micSeg?.Flush();
        _mic?.Dispose(); _mic = null;
        PlaybackSource[] srcs; lock (_srcGate) srcs = _sources.ToArray();
        foreach (var s in srcs) try { s.Finish(); } catch (Exception ex) { Log.Error("loopback finish", ex); }
        await FinishAsync();
        return FilePath;
    }

    async Task FinishAsync()
    {
        _queue.CompleteAdding();
        var w = _worker;
        if (w != null) await Task.Run(() => w.Join());
        _clock.Stop();
        if (_settings.MeetingSpeakerLabels)
        {
            Status?.Invoke("Telling the other voices apart");
            await Task.Run(LabelSpeakers);
        }
        FinalizeFile();
        CleanUpAudio();
        Status?.Invoke("Saved");
    }

    /// <summary>
    /// Headless meeting (Vox.exe --meeting-test): the microphone and each playback device are given as whole recordings
    /// already on the meeting clock. Runs the same phrase cutting, transcription, echo removal and speaker labeling.
    /// </summary>
    internal async Task<string?> RunRecordedAsync(string title, float[]? mic, IReadOnlyList<float[]> playback)
    {
        Begin(title);
        IsRunning = true;
        StartWorker();
        _micSeg = new Segmenter("You", 0, _queue.Add, source: 0);
        if (mic != null) { _micSeg.Push(mic); }
        _micSeg.Flush();
        for (int i = 0; i < playback.Count; i++)
        {
            var src = new PlaybackSource(i + 1, null, _clock, _queue.Add, Path.Combine(_workDir, $"{_startedAt:yyyyMMdd-HHmmss}-{i + 1}.wav"), offsetSec: 0);
            lock (_srcGate) _sources.Add(src);
            src.PushAll(playback[i]);
            src.Finish();
        }
        IsRunning = false;
        await FinishAsync();
        return FilePath;
    }

    // ----- Speaker labels -----

    /// <summary>
    /// Runs speaker diarization on each playback device's audio and relabels its lines "Speaker 1", "Speaker 2", ...
    /// (numbered in order of first speaking). A line that holds two people's turns is split at the change and each
    /// part is transcribed again on its own. Your microphone stays "You".
    /// </summary>
    void LabelSpeakers()
    {
        PlaybackSource[] srcs; lock (_srcGate) srcs = _sources.Where(s => s.WavPath != null && s.HeardSound).ToArray();
        List<Entry> entries; lock (_gate) entries = _entries.ToList();
        if (srcs.Length == 0 || !entries.Any(e => e.Source > 0)) return;
        SpeakerDiarizer? dz;
        try { dz = SpeakerDiarizer.TryCreate(_data.ModelsDir, _settings.Threads); }
        catch (Exception ex) { Log.Error("diarizer load", ex); _speakerNote = "Speaker labels were skipped (the speaker models could not be loaded)."; return; }
        if (dz == null) { _speakerNote = "Other people are labeled \"Others\". Install the speaker models in Settings to tell their voices apart."; return; }

        var sw = Stopwatch.StartNew();
        var cleanSettings = new AppSettings { RemoveFillers = true, VoiceCommands = false };
        var prompt = TextCleaner.BuildPrompt(_data.Dictionary);
        var result = entries.Where(e => e.Source == 0).ToList();
        var labeled = new List<(Entry E, int Src, int Spk)>();
        using (dz)
        {
            foreach (var src in srcs)
            {
                float[] audio;
                try { audio = ReadWav(src.WavPath!); } catch (Exception ex) { Log.Error("diarize read", ex); continue; }
                List<SpeakerTurn> turns;
                var swd = Stopwatch.StartNew(); try { turns = dz.Run(audio); } catch (Exception ex) { Log.Error("diarize run", ex); turns = new(); } Log.Info($"meeting: diarize {audio.Length / 16000.0:0} s of audio in {swd.ElapsedMilliseconds} ms");
                var mine = entries.Where(e => e.Source == src.Index).ToList();
                if (turns.Count == 0) { foreach (var e in mine) labeled.Add((e, src.Index, -1)); continue; }
                int first = labeled.Count;
                foreach (var e in mine)
                {
                    double s = e.StartSec - src.OffsetSec, t = e.EndSec - src.OffsetSec;
                    var runs = SpeakerDiarizer.RunsWithin(turns, s, t);
                    if (runs.Count <= 1)
                    {
                        labeled.Add((e, src.Index, runs.Count == 1 ? runs[0].Speaker : SpeakerDiarizer.Nearest(turns, (s + t) / 2)));
                        continue;
                    }
                    // two or more people in one line: transcribe each person's part separately
                    var parts = new List<(Entry, int, int)>();
                    foreach (var r in runs)
                    {
                        int a = (int)Math.Max(0, (r.Start - 0.15) * 16000), b = (int)Math.Min(audio.Length, (r.End + 0.15) * 16000);
                        if (b - a < 8000) continue;
                        var piece = audio[a..b];
                        var raw = _engine.TranscribeAsync(AudioPrep.Condition(piece), _settings.Language, _settings.Threads, prompt).GetAwaiter().GetResult();
                        var text = TextCleaner.Clean(raw, cleanSettings, _data.Dictionary, Array.Empty<Snippet>());
                        if (text.Length >= 2) parts.Add((new Entry(src.OffsetSec + r.Start, src.OffsetSec + r.End, "Others", text, src.Index), src.Index, r.Speaker));
                    }
                    if (parts.Count >= 2) labeled.AddRange(parts);
                    else labeled.Add((e, src.Index, runs.OrderByDescending(r => r.End - r.Start).First().Speaker));
                }
                // second pass: check each line's own voice against the people found
                try
                {
                    int count = labeled.Count - first;
                    var embs = new float[]?[count]; var labs = new int[count];
                    for (int k = 0; k < count; k++)
                    {
                        var (e, _, spk) = labeled[first + k];
                        labs[k] = spk;
                        int a = (int)Math.Max(0, (e.StartSec - src.OffsetSec) * 16000), b = (int)Math.Min(audio.Length, (e.EndSec - src.OffsetSec) * 16000);
                        if (b - a >= 19200) embs[k] = dz.Embed(audio[a..b]);       // 1.2 s or more
                    }
                    var fixedLabs = SpeakerDiarizer.Refine(embs, labs);
                    for (int k = 0; k < count; k++)
                        if (fixedLabs[k] != labs[k]) labeled[first + k] = labeled[first + k] with { Spk = fixedLabs[k] };
                }
                catch (Exception ex) { Log.Error("speaker refine", ex); }
            }
        }
        // number people in the order they first spoke (across devices)
        var names = new Dictionary<(int, int), string>();
        int next = 1;
        foreach (var l in labeled.OrderBy(l => l.E.StartSec))
        {
            if (l.Spk < 0) continue;
            if (!names.ContainsKey((l.Src, l.Spk))) names[(l.Src, l.Spk)] = "Speaker " + next++;
        }
        foreach (var l in labeled)
            result.Add(l.E with { Speaker = l.Spk >= 0 ? names[(l.Src, l.Spk)] : "Others" });
        lock (_gate) { _entries.Clear(); _entries.AddRange(result); }
        _speakerNote = (next - 1) switch
        {
            0 => "",
            1 => "Vox heard one other voice (\"Speaker 1\").",
            _ => $"Vox heard {next - 1} other voices, labeled Speaker 1 to {next - 1} in the order they first spoke. Speaker labels are automatic and can be wrong when people talk over each other or sound alike. Use \"Rename speakers\" in Vox to put names on them."
        };
        Log.Info($"meeting: speaker labels in {sw.ElapsedMilliseconds} ms, {next - 1} speakers");
    }

    static float[] ReadWav(string path)
    {
        using var r = new WaveFileReader(path);
        var bytes = new byte[r.Length]; int read = 0;
        while (read < bytes.Length) { int n = r.Read(bytes, read, bytes.Length - read); if (n <= 0) break; read += n; }
        var f = new float[read / 2];
        for (int i = 0; i < f.Length; i++) f[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
        return f;
    }

    void CleanUpAudio()
    {
        PlaybackSource[] srcs; lock (_srcGate) { srcs = _sources.ToArray(); _sources.Clear(); }
        foreach (var s in srcs)
        {
            s.Dispose();
            if (s.WavPath == null || !File.Exists(s.WavPath)) continue;
            try
            {
                // "Keep audio" also keeps what the meeting played, next to the transcript; otherwise it is deleted now
                if (_settings.KeepAudio && s.HeardSound && FilePath != null)
                    File.Move(s.WavPath, Path.ChangeExtension(FilePath, null) + (srcs.Length > 1 ? $" (others {s.Index}).wav" : " (others).wav"), true);
                else File.Delete(s.WavPath);
            }
            catch (Exception ex) { Log.Error("meeting audio cleanup", ex); }
        }
    }

    // ----- Markdown output -----
    static string Ts(double sec) => TimeSpan.FromSeconds(Math.Max(0, sec)).ToString(@"hh\:mm\:ss");

    string Header(TimeSpan duration)
    {
        string others;
        lock (_srcGate)
        {
            var heard = _sources.Where(s => s.HeardSound).Select(s => s.Name).Distinct().ToList();
            others = _sources.Count == 0 ? " only"
                : heard.Count == 0 ? " and system audio (\"Others\", nothing played)"
                : $" and system audio (\"Others\", from {string.Join(", ", heard)})";
        }
        return $"# {_title}\n\n" +
            $"- **Date:** {_startedAt:dddd, MMMM d, yyyy} at {_startedAt:h:mm tt}\n" +
            $"- **Duration:** {Ts(duration.TotalSeconds)}\n" +
            $"- **Audio:** microphone (\"You\"){others}\n" +
            (_speakerNote.Length > 0 ? $"- **Speakers:** {_speakerNote}\n" : "") +
            $"- **Transcribed by:** Vox, locally on this PC ({_engine.ModelName}). No audio or text left this computer.\n\n" +
            "## Transcript\n\n";
    }

    void WriteHeader() { try { File.WriteAllText(FilePath!, Header(TimeSpan.Zero) + "_Recording in progress..._\n\n", new UTF8Encoding(true)); } catch (Exception ex) { Log.Error("meeting header", ex); } }

    void AppendLine(double start, string speaker, string text)
    {
        try { File.AppendAllText(FilePath!, $"**[{Ts(start)}] {speaker}:** {text}\n\n", new UTF8Encoding(true)); } catch { }
    }

    void FinalizeFile()
    {
        try
        {
            List<Entry> list;
            lock (_gate) list = _entries.OrderBy(e => e.StartSec).ToList();
            list = DropEchoes(list);
            var header = Header(_clock.Elapsed);
            var actions = ActionItems.Markdown(ActionItems.Find(list.Select(e => (e.StartSec, e.Speaker, e.Text))), Ts);
            if (actions.Length > 0) header = header.Replace("## Transcript\n\n", actions + "## Transcript\n\n");
            var sb = new StringBuilder(header);
            foreach (var e in list) sb.Append($"**[{Ts(e.StartSec)}] {e.Speaker}:** {e.Text}\n\n");
            if (list.Count == 0) sb.Append("_No speech was detected._\n");
            File.WriteAllText(FilePath!, sb.ToString(), new UTF8Encoding(true));
            FinalLines = list.Select(e => (e.StartSec, e.EndSec, e.Speaker, e.Text, e.Source)).ToList();
        }
        catch (Exception ex) { Log.Error("meeting finalize", ex); }
    }

    /// <summary>With speakers (not a headset) the mic hears the meeting too. Drop a "You" line that repeats an overlapping line from a playback device.</summary>
    static List<Entry> DropEchoes(List<Entry> list)
    {
        static HashSet<string> Words(string s) => s.ToLowerInvariant().Split(new[] { ' ', ',', '.', '?', '!', ';', ':', '"' }, StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var others = list.Where(e => e.Source > 0).ToList();
        return list.Where(e =>
        {
            if (e.Source != 0) return true;
            var w = Words(e.Text);
            if (w.Count < 3) return true;
            foreach (var o in others.Where(o => Math.Abs(o.StartSec - e.StartSec) < 8))
            {
                var ow = Words(o.Text);
                double inter = w.Count(ow.Contains), union = w.Count + ow.Count - inter;
                if (union > 0 && inter / union > 0.55) return false;
            }
            return true;
        }).ToList();
    }

    // ----- Renaming speakers in a saved transcript -----

    /// <summary>Speaker names used in a saved transcript, in order of first appearance.</summary>
    public static List<string> SpeakersIn(string mdPath)
    {
        var found = new List<string>();
        foreach (System.Text.RegularExpressions.Match m in LineRx.Matches(File.ReadAllText(mdPath)))
        {
            var name = m.Groups[2].Value;
            if (!found.Contains(name)) found.Add(name);
        }
        return found;
    }

    /// <summary>Replaces speaker names in a saved transcript (only in the "**[time] Name:**" line labels).</summary>
    public static void RenameSpeakers(string mdPath, IReadOnlyDictionary<string, string> renames)
    {
        var text = File.ReadAllText(mdPath);
        var updated = LineRx.Replace(text, m =>
            renames.TryGetValue(m.Groups[2].Value, out var to) && !string.IsNullOrWhiteSpace(to)
                ? $"**[{m.Groups[1].Value}] {to.Trim().Replace("*", "").Replace(":", "")}:**" : m.Value);
        // and the names in the action-item list: "_(Speaker 1, 00:12:04)_"
        updated = ActionRx.Replace(updated, m =>
            renames.TryGetValue(m.Groups[1].Value, out var to) && !string.IsNullOrWhiteSpace(to)
                ? $"_({to.Trim().Replace("*", "").Replace(",", "")}, {m.Groups[2].Value})_" : m.Value);
        if (updated != text) AtomicFile.WriteAllText(mdPath, updated);
    }

    static readonly System.Text.RegularExpressions.Regex ActionRx =
        new(@"_\(([^,()\n]+), (\d\d:\d\d:\d\d)\)_", System.Text.RegularExpressions.RegexOptions.Compiled);
    static readonly System.Text.RegularExpressions.Regex LineRx =
        new(@"\*\*\[(\d\d:\d\d:\d\d)\] ([^*\n:]+):\*\*", System.Text.RegularExpressions.RegexOptions.Compiled);
}
