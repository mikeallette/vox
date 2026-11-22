using System.Diagnostics;
using System.Windows.Threading;

namespace Vox.Core;

public enum VoxState { Idle, Listening, Processing, Done, Notice }

/// <summary>Orchestrates one dictation: record -> transcribe (local) -> clean up -> insert at the caret -> save to history.</summary>
public sealed class DictationController
{
    readonly AppSettings _settings;
    readonly DataStore _data;
    readonly SpeechEngine _engine;
    readonly TextInjector _injector = new();
    readonly MicRecorder _mic = new();
    readonly DispatcherTimer _maxTimer = new() { Interval = TimeSpan.FromMinutes(10) };

    bool _busy;
    volatile LiveTranscriber? _live;
    DictationMode _mode;
    (string process, string title) _target;

    public event Action<VoxState, string>? StateChanged;
    public event Action<float>? Level;
    public VoxState State { get; private set; } = VoxState.Idle;
    public bool IsListening => State == VoxState.Listening;

    public DictationController(AppSettings s, DataStore d, SpeechEngine e)
    {
        _settings = s; _data = d; _engine = e;
        _mic.Level += l =>
        {
            Level?.Invoke(l);
            var w = _levels;
            if (w != null && w.Push(l))
                _ui?.Post(_ => { if (State == VoxState.Listening && ReferenceEquals(w, _levels)) Set(VoxState.Listening, ListeningLabel()); }, null);
        };
        _mic.Chunk += c => _live?.Push(c);
        _maxTimer.Tick += (_, _) => { _maxTimer.Stop(); if (IsListening) _ = FinishAsync(); };
    }

    void Set(VoxState st, string msg = "") { State = st; StateChanged?.Invoke(st, msg); }

    volatile LevelWatch? _levels;
    SynchronizationContext? _ui;
    public const string QuietLabel = "Mic is very quiet - move closer";
    string ListeningLabel() => _levels?.IsLow == true ? QuietLabel : _mode == DictationMode.HandsFree ? "Hands-free" : "Listening";

    /// <summary>Remembers this dictation's speech level so the warning follows the user's own voice and mic.</summary>
    void RememberLevel(float level)
    {
        if (level <= 0) return;
        var list = _settings.RecentSpeechLevels;
        list.Add((float)Math.Round(level, 5));
        while (list.Count > 20) list.RemoveAt(0);
        try { _settings.Save(); } catch (Exception ex) { Log.Error("save speech level", ex); }
    }

    public void Start(DictationMode mode)
    {
        if (_busy) { Log.Info($"dictation: start ({mode}) ignored, still finishing the last one"); return; }
        if (State == VoxState.Listening) { _mode = mode; Set(VoxState.Listening, ListeningLabel()); return; }   // PTT -> hands-free switch
        if (!_engine.IsLoaded) { Notice("No speech model loaded. Open Vox > Settings."); return; }
        try
        {
            _target = Native.ForegroundInfo();
            _mode = mode;
            _engine.Hints = _settings.AccurateHints ? TextCleaner.HintTerms(_data.Dictionary) : Array.Empty<string>();
            // accurate mode (Qwen3) needs the whole sentence: in 6 s live pieces it fell from 93.3% to 91.2% words right
            // (2026-11-11 replay), below Parakeet live, while still making the user wait ~3.5 s; so it always runs whole-clip
            _live = _settings.LiveTranscribe && !_engine.WantsWholeClip
                ? new LiveTranscriber(_engine, _settings.Language, _settings.Threads, TextCleaner.BuildPrompt(_data.Dictionary))
                : null;
            _ui = SynchronizationContext.Current;
            _levels = new LevelWatch(LevelWatch.Typical(_settings.RecentSpeechLevels));
            _mic.Start(_settings.MicrophoneName);
            _maxTimer.Stop(); _maxTimer.Start();
            Set(VoxState.Listening, ListeningLabel());
        }
        catch (Exception ex)
        {
            Log.Error("mic start", ex);
            Notice("Can't open the microphone. Check Windows privacy settings.");
        }
    }

    public void Cancel(bool silent)
    {
        if (State != VoxState.Listening) return;
        // The keys were already let go and the dictation is being finished. A stray key event in the next 180 ms
        // (seen 2026-11-10: it threw away the live transcript and the level check) must not cancel it.
        if (_busy) { Log.Info($"dictation: cancel ignored, already finishing (silent {silent})"); return; }
        Log.Info($"dictation: cancelled (silent {silent})");
        _maxTimer.Stop();
        _mic.Cancel();
        _live?.Cancel(); _live = null;
        _levels = null;
        if (silent) Set(VoxState.Idle); else Notice("Cancelled");
    }

    public async Task FinishAsync()
    {
        if (State != VoxState.Listening || _busy) return;
        _busy = true;
        _maxTimer.Stop();
        var live = _live;                                 // take these now, before the await
        var levels = _levels;
        try
        {
            await Task.Delay(180);                       // keep the tail of the last word
            var samples = _mic.Stop();
            _live = null; _levels = null;
            float peak = _mic.PeakLevel;
            double secs = samples.Length / (double)MicRecorder.SampleRate;
            float speech = levels?.SpeechLevel() ?? 0;
            bool quiet = levels != null && speech < levels.Threshold && secs >= 1.0;
            Log.Info($"dictation: {secs:0.0}s audio, mic peak {peak:0.0000}, speech level {speech:0.0000} (warn below {levels?.Threshold ?? 0:0.0000}{(quiet ? ", QUIET" : "")}), mode {_mode}, target {_target.process}");
            if (secs >= 1.0 && peak >= 0.003) RememberLevel(speech);
            if (secs < 0.35) { live?.Cancel(); Set(VoxState.Idle); return; }
            if (peak < 0.003) { live?.Cancel(); Log.Info("dictation: no sound detected"); Notice("No sound heard. Check your microphone."); return; }

            Set(VoxState.Processing, "Transcribing");
            var sw = Stopwatch.StartNew();                // time the user actually waits after letting go
            int phrases = live?.SegmentCount ?? 0;
            string? raw = live != null ? await live.FinishAsync() : null;
            bool wasLive = raw != null;
            raw ??= await SpeechGate.TranscribeAsync(_engine, samples, _settings.Language, _settings.Threads, TextCleaner.BuildPrompt(_data.Dictionary), condition: true);
            int ms = (int)sw.ElapsedMilliseconds;

            var text = TextCleaner.Clean(raw, _settings, _data.Dictionary, _data.Snippets);
            Log.Info($"dictation: waited {ms} ms after release ({(wasLive ? "live, " + phrases + "+ phrases" + (live!.TailMerged ? ", short last piece joined" : "") : "whole clip")}) -> {text.Length} chars");
            if (string.IsNullOrWhiteSpace(text)) { Notice("Didn't catch that"); return; }

            var toInsert = _settings.AppendTrailingSpace ? text + " " : text;
            var inserted = await _injector.InsertAsync(toInsert, _settings.Insert);

            if (_settings.SaveHistory)
            {
                string? audio = null;
                var entry = new HistoryEntry { Text = text, RawText = raw, AudioSeconds = Math.Round(secs, 1), TranscribeMs = ms, App = _target.process, Model = _engine.ModelName };
                if (_settings.KeepAudio)
                {
                    try { Directory.CreateDirectory(_data.AudioDir); audio = Path.Combine(_data.AudioDir, $"{entry.Time:yyyyMMdd-HHmmss}.wav"); MicRecorder.SaveWav(audio, samples); } catch { }
                }
                _data.AddHistory(entry);
            }
            LastText = text;
            if (inserted == InsertResult.ClipboardOnly) { AdminNotice(); return; }
            if (inserted == InsertResult.Failed) { Notice("Couldn't paste. Use Alt+Shift+Z to try again."); return; }
            if (quiet)
            {
                // still inserted (it may be fine), but say why it might be wrong
                Set(VoxState.Notice, "Mic was very quiet - check the text");
                await Task.Delay(2500);
                if (State == VoxState.Notice) Set(VoxState.Idle);
                return;
            }
            Set(VoxState.Done, $"{ms / 1000.0:0.0}s");
            await Task.Delay(700);
            if (State == VoxState.Done) Set(VoxState.Idle);
        }
        catch (Exception ex)
        {
            Log.Error("dictation", ex);
            Notice("Something went wrong. See the log.");
        }
        finally { _busy = false; }
    }

    public string LastText { get; private set; } = "";

    public async Task PasteLastAsync()
    {
        var t = LastText.Length > 0 ? LastText : _data.History.FirstOrDefault()?.Text ?? "";
        if (t.Length == 0) { Notice("Nothing to paste yet"); return; }
        if (await _injector.InsertAsync(t, _settings.Insert) == InsertResult.ClipboardOnly) AdminNotice();
    }

    public void CopyLast()
    {
        var t = LastText.Length > 0 ? LastText : _data.History.FirstOrDefault()?.Text ?? "";
        if (t.Length == 0) { Notice("Nothing to copy yet"); return; }
        TextInjector.SetClipboard(t);
        Notice("Copied last transcript");
    }

    /// <summary>Admin windows block Vox's keystrokes, so the text waits on the clipboard; say so for longer.</summary>
    async void AdminNotice()
    {
        Set(VoxState.Notice, "Admin window: press Ctrl+V to paste");
        await Task.Delay(4500);
        if (State == VoxState.Notice) Set(VoxState.Idle);
    }

    async void Notice(string msg)
    {
        Set(VoxState.Notice, msg);
        await Task.Delay(2200);
        if (State == VoxState.Notice) Set(VoxState.Idle);
    }
}
