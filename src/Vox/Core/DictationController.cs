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

    /// <summary>Loads (once) and returns the accurate-mode engine, or null when no .gguf model is installed.</summary>
    public Func<Task<SpeechEngine?>>? GetAccurateEngine { get; set; }
    /// <summary>True when an accurate-mode model is installed (cheap check, no loading).</summary>
    public Func<bool>? AccurateAvailable { get; set; }

    bool _useAccurate;                                   // this dictation goes to accurate mode (per-app setting)
    float[]? _lastSamples;                               // audio of the last dictation, for "re-hear"
    (string text, IntPtr window, long tick)? _lastInsert; // what Vox last typed, where and when (for scratch that / re-hear)
    HistoryEntry? _lastEntry;                            // its history entry, replaced or removed along with it
    bool _lastWasAccurate;                               // the last dictation already went through accurate mode
    volatile bool _rehearing;

    /// <summary>The calculator and the trailing "press enter" (the same for a fresh dictation and a re-hear).</summary>
    static (string text, bool pressEnter) ApplyTextActions(string text, string raw)
    {
        if (VoiceActions.TryCalculate(text, out var answer) || VoiceActions.TryCalculate(raw, out answer))
        {
            Log.Info("dictation: calculated an answer");      // not the words: the log must not hold dictated text
            text = answer;
        }
        bool enter = VoiceActions.SplitPressEnter(text, out var rest);
        return (enter ? rest : text, enter);
    }

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
        if (_busy)
        {
            Log.Info($"dictation: start ({mode}) ignored, still finishing the last one");
            // say so (a re-hear can take several seconds), unless it's the stray start from a shared shortcut mid-finish
            if (State == VoxState.Processing && _rehearing) Notice("Still re-hearing. Try again in a moment");
            return;
        }
        if (State == VoxState.Listening) { _mode = mode; Set(VoxState.Listening, ListeningLabel()); return; }   // PTT -> hands-free switch
        if (!_engine.IsLoaded) { Notice("No speech model loaded. Open Vox > Settings."); return; }
        try
        {
            _target = Native.ForegroundInfo();
            _mode = mode;
            _engine.Hints = _settings.AccurateHints ? TextCleaner.HintTerms(_data.Dictionary) : Array.Empty<string>();
            // per-app accurate mode (Outlook, Word...): start loading it now so the load overlaps the speaking
            _useAccurate = !_engine.WantsWholeClip && AccurateAvailable?.Invoke() == true
                           && _settings.AccurateApps.Any(a => a.Equals(_target.process, StringComparison.OrdinalIgnoreCase));
            if (_useAccurate) { Log.Info($"dictation: accurate mode for {_target.process}"); _ = GetAccurateEngine?.Invoke(); }
            // accurate mode (Qwen3) needs the whole sentence: in 6 s live pieces it fell from 93.3% to 91.2% words right
            // (2026-11-11 replay), below Parakeet live, while still making the user wait ~3.5 s; so it always runs whole-clip
            _live = _settings.LiveTranscribe && !_engine.WantsWholeClip && !_useAccurate
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
            var engine = _engine;
            if (raw == null && _useAccurate && GetAccurateEngine != null)
            {
                try { engine = await GetAccurateEngine() ?? _engine; }
                catch (Exception ex) { Log.Error("accurate mode load", ex); }
            }
            if (engine != _engine) engine.Hints = _engine.Hints;
            raw ??= await SpeechGate.TranscribeAsync(engine, samples, _settings.Language, _settings.Threads, TextCleaner.BuildPrompt(_data.Dictionary), condition: true);
            int ms = (int)sw.ElapsedMilliseconds;

            var text = TextCleaner.Clean(raw, _settings, _data.Dictionary, _data.Snippets);
            Log.Info($"dictation: waited {ms} ms after release ({(wasLive ? "live, " + phrases + "+ phrases" + (live!.TailMerged ? ", short last piece joined" : "") : "whole clip")}{(engine != _engine ? ", accurate mode" : "")}) -> {text.Length} chars");
            if (string.IsNullOrWhiteSpace(text)) { Notice("Didn't catch that"); return; }

            // ----- spoken actions on the whole dictation -----
            bool pressEnter = false;
            if (_settings.VoiceCommands)
            {
                if (VoiceActions.IsScratchThat(text)) { await ScratchLastAsync(); return; }
                if (VoiceActions.TryNoteToSelf(text, out var note))
                {
                    try { VoiceActions.SaveNote(_data.Root, note); Log.Info("dictation: saved a note to self"); Notice("Saved to Notes"); }
                    catch (Exception ex) { Log.Error("note to self", ex); Notice("Couldn't save the note. See the log."); }
                    return;
                }
                (text, pressEnter) = ApplyTextActions(text, raw);
            }

            _lastSamples = samples;
            _lastWasAccurate = engine != _engine;
            var inserted = InsertResult.Inserted;
            if (text.Length > 0)
            {
                var toInsert = _settings.AppendTrailingSpace && !pressEnter ? text + " " : text;
                inserted = await _injector.InsertAsync(toInsert, _settings.Insert);
                _lastInsert = inserted == InsertResult.Inserted && !pressEnter ? (toInsert, Native.GetForegroundWindow(), Environment.TickCount64) : null;
            }
            if (pressEnter && inserted == InsertResult.Inserted)
            {
                await Task.Delay(60);
                if (!await TextInjector.PressEnterAsync())   // admin window: Windows drops the key
                {
                    Log.Info("press enter: focused window runs as administrator; Enter not sent");
                    Set(VoxState.Notice, "Admin window: press Enter yourself");
                    _lastInsert = null;
                    await Task.Delay(3500);
                    if (State == VoxState.Notice) Set(VoxState.Idle);
                    return;
                }
                _lastInsert = null;
            }

            _lastEntry = null;
            if (_settings.SaveHistory && !_settings.PrivateMode && text.Length > 0)
            {
                string? audio = null;
                var entry = new HistoryEntry { Text = text, RawText = raw, AudioSeconds = Math.Round(secs, 1), TranscribeMs = ms, App = _target.process, Model = engine.ModelName };
                if (_settings.KeepAudio)
                {
                    try { Directory.CreateDirectory(_data.AudioDir); audio = Path.Combine(_data.AudioDir, $"{entry.Time:yyyyMMdd-HHmmss}.wav"); MicRecorder.SaveWav(audio, samples); } catch { }
                }
                _data.AddHistory(entry);
                _lastEntry = entry;
            }
            if (text.Length > 0) LastText = text;
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

    /// <summary>
    /// Vox may delete what it last typed only if the same window still has focus, nothing was typed since, and the text
    /// is one line (Backspace counts characters; a line break can be one or two of them depending on the app).
    /// Mouse clicks can't be seen, so this is also limited to 2 minutes.
    /// </summary>
    bool CanReplaceLast(out string why)
    {
        why = "";
        if (_lastInsert is not { } li) { why = "nothing to replace"; return false; }
        if (Native.GetForegroundWindow() != li.window) { why = "a different window has focus"; return false; }
        if (HotkeyService.LastUserKeyTick > li.tick) { why = "you typed after it"; return false; }
        if (Environment.TickCount64 - li.tick > 120_000) { why = "it was more than 2 minutes ago"; return false; }
        if (li.text.Contains('\n') || li.text.Length > 2000) { why = "it has more than one line"; return false; }
        return true;
    }

    async Task ScratchLastAsync()
    {
        if (!CanReplaceLast(out var why))
        {
            Log.Info($"scratch that: not removed ({why})");
            Notice(_lastInsert == null ? "Nothing to scratch" : "Can't remove it safely. Press Ctrl+Z");
            return;
        }
        await TextInjector.BackspaceAsync(_lastInsert!.Value.text);
        Log.Info($"scratch that: removed {_lastInsert!.Value.text.Length} characters");
        _lastInsert = null;
        // gone from the document, so gone from history and from "paste last" too
        if (_lastEntry != null) { _data.DeleteHistory(_lastEntry); _lastEntry = null; }
        LastText = "";
        _lastSamples = null;
        Notice("Removed");
    }

    /// <summary>Runs the last dictation's audio through accurate mode and swaps in the new text when it differs.</summary>
    public async Task RehearLastAsync()
    {
        if (_busy || State == VoxState.Listening) return;
        if (_lastSamples == null) { Notice("Nothing to re-hear yet"); return; }
        if (_engine.WantsWholeClip || _lastWasAccurate) { Notice("That one already used accurate mode"); return; }
        if (AccurateAvailable?.Invoke() != true || GetAccurateEngine == null) { Notice("Accurate mode isn't installed"); return; }
        _busy = true; _rehearing = true;
        try
        {
            Set(VoxState.Processing, "Re-hearing");
            var sw = Stopwatch.StartNew();
            var acc = await GetAccurateEngine();
            if (acc == null) { Notice("Accurate mode isn't installed"); return; }
            acc.Hints = _settings.AccurateHints ? TextCleaner.HintTerms(_data.Dictionary) : Array.Empty<string>();
            var raw = await SpeechGate.TranscribeAsync(acc, _lastSamples, _settings.Language, _settings.Threads, "", condition: true);
            var text = TextCleaner.Clean(raw, _settings, _data.Dictionary, _data.Snippets);
            // same spoken actions as the first time ("calculate ...", "... press enter"), so the command words never get typed;
            // Enter is not pressed again
            if (_settings.VoiceCommands) text = ApplyTextActions(text, raw).text;
            Log.Info($"re-hear: {sw.ElapsedMilliseconds} ms, {text.Length} chars");
            if (string.IsNullOrWhiteSpace(text)) { Notice("Accurate mode heard nothing"); return; }
            if (text == LastText) { Notice("Accurate mode heard the same"); return; }
            _lastWasAccurate = true;

            var toInsert = _settings.AppendTrailingSpace ? text + " " : text;
            if (CanReplaceLast(out var why))
            {
                await TextInjector.BackspaceAsync(_lastInsert!.Value.text);
                var r = await _injector.InsertAsync(toInsert, _settings.Insert);
                _lastInsert = r == InsertResult.Inserted ? (toInsert, Native.GetForegroundWindow(), Environment.TickCount64) : null;
                LastText = text;
                if (_settings.SaveHistory && !_settings.PrivateMode)
                {
                    // the re-heard text replaces the first version in history rather than adding a second entry
                    if (_lastEntry != null) _data.DeleteHistory(_lastEntry);
                    _lastEntry = new HistoryEntry { Text = text, RawText = raw, AudioSeconds = Math.Round(_lastSamples.Length / (double)MicRecorder.SampleRate, 1), TranscribeMs = (int)sw.ElapsedMilliseconds, App = _target.process, Model = acc.ModelName };
                    _data.AddHistory(_lastEntry);
                }
                Set(VoxState.Done, "Replaced");
                await Task.Delay(900);
                if (State == VoxState.Done) Set(VoxState.Idle);
            }
            else
            {
                Log.Info($"re-hear: left on the clipboard ({why})");
                TextInjector.SetClipboard(text);
                LastText = text;
                Set(VoxState.Notice, "Better version copied: press Ctrl+V");
                await Task.Delay(4000);
                if (State == VoxState.Notice) Set(VoxState.Idle);
            }
        }
        catch (Exception ex) { Log.Error("re-hear", ex); Notice("Something went wrong. See the log."); }
        finally { _busy = false; _rehearing = false; }
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
