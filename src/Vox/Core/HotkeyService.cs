using static Vox.Core.Native;

namespace Vox.Core;

public enum DictationMode { PushToTalk, HandsFree }

/// <summary>
/// System-wide keyboard hook (WH_KEYBOARD_LL) on its own thread, so modifier-only chords such as
/// Ctrl+Win work (Electron-style globalShortcut cannot do that). Every shortcut is a user-chosen KeyCombo.
///   hold the dictation keys       push-to-talk (release = finish)
///   dictation keys + Space        switch to / stop hands-free
///   double-tap the dictation keys hands-free
///   Esc                           cancel
///   paste-last / copy-last keys   paste / copy the last transcript (default Alt+Shift+Z / X)
/// BeginCapture records the next combination the user presses (for the Settings shortcut boxes); while it runs,
/// all keys are swallowed so pressing e.g. Win does not open Start and the current shortcut does not dictate.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    public event Action<DictationMode>? DictationStarted;
    public event Action? DictationFinished;            // user is done: transcribe
    public event Action<bool>? DictationCancelled;     // true = silent (it was just a tap)
    public event Action? PasteLastRequested;
    public event Action? CopyLastRequested;

    readonly SynchronizationContext _ui;
    readonly bool[] _down = new bool[256];
    readonly LowLevelKeyboardProc _proc;
    Thread? _thread;
    uint _threadId;
    IntPtr _hook;

    enum State { Idle, Ptt, HandsFree }
    State _state = State.Idle;
    bool _chordDown, _ignoreRelease, _spaceSwallowed, _escSwallowed;
    readonly HashSet<int> _swallowUntilUp = new();
    DateTime _chordDownAt = DateTime.MinValue, _lastTapAt = DateTime.MinValue;

    public KeyCombo Dictation { get; set; } = KeyCombo.DefaultDictation;
    public KeyCombo PasteLast { get; set; } = KeyCombo.DefaultPasteLast;
    public KeyCombo CopyLast { get; set; } = KeyCombo.DefaultCopyLast;
    public bool Enabled { get; set; } = true;

    /// <summary>Where modifier state is re-read from (the OS normally; the self-test supplies its own).</summary>
    internal Func<int, bool> OsKeyDown { get; set; } = IsDown;
    internal bool TestMode { get; set; }

    const int TapMs = 280, DoubleTapMs = 450;

    public HotkeyService(SynchronizationContext ui)
    {
        _ui = ui;
        _proc = HookProc;
    }

    public void Start()
    {
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero) Log.Error("SetWindowsHookEx failed: " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            ready.Set();
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref msg); DispatchMessage(ref msg); }
            if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        }) { IsBackground = true, Name = "Vox.KeyboardHook" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(2000);
    }

    /// <summary>Tell the hook that dictation ended elsewhere (error, timeout) so state resets.</summary>
    public void ResetState() { _state = State.Idle; _ignoreRelease = false; }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, 0x0012 /*WM_QUIT*/, UIntPtr.Zero, IntPtr.Zero);
    }

    // ----- recording a new shortcut -----
    Action<KeyCombo?>? _capture;
    readonly HashSet<int> _capHeld = new();
    KeyCombo? _capBest;

    /// <summary>
    /// Records the next key combination: the largest set of keys held at once, finished when all keys are released.
    /// Esc alone cancels (done gets null). done runs on the UI thread.
    /// </summary>
    public void BeginCapture(Action<KeyCombo?> done)
    {
        lock (_capHeld) { _capHeld.Clear(); _capBest = null; _capture = done; }
    }

    public void CancelCapture()
    {
        Action<KeyCombo?>? d; lock (_capHeld) { d = _capture; _capture = null; }
        if (d != null) Post(() => d(null));
    }

    public bool IsCapturing => _capture != null;

    bool HandleCapture(int vk, bool isDown, bool isUp)
    {
        Action<KeyCombo?>? finish = null; KeyCombo? result = null;
        lock (_capHeld)
        {
            if (_capture == null) return false;
            if (isDown)
            {
                if (vk == VK_ESCAPE && _capHeld.Count == 0) { finish = _capture; _capture = null; }
                else
                {
                    _capHeld.Add(vk);
                    var c = KeyCombo.FromKeys(_capHeld);
                    if (_capBest == null || Count(c) >= Count(_capBest)) _capBest = c;
                }
            }
            else if (isUp)
            {
                _capHeld.Remove(vk);
                if (_capHeld.Count == 0 && _capBest != null) { finish = _capture; result = _capBest; _capture = null; }
            }
        }
        if (finish != null) Post(() => finish(result));
        return true;   // nothing reaches other apps while recording
    }

    static int Count(KeyCombo c) => (c.Ctrl ? 1 : 0) + (c.Alt ? 1 : 0) + (c.Shift ? 1 : 0) + (c.Win ? 1 : 0) + c.Keys.Length;

    // ----- hook -----

    IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_hook, nCode, wParam, lParam);
        var k = System.Runtime.InteropServices.Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        // Skip only Vox's own synthetic keys (paste, typing, Start-menu mask). Keys injected by other tools
        // (remappers, remote desktop, on-screen keyboards) still count.
        if ((k.flags & LLKHF_INJECTED) != 0 && k.dwExtraInfo == VoxTag)
            return CallNextHookEx(_hook, nCode, wParam, lParam);

        int msg = (int)wParam;
        bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
        bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;
        int vk = (int)k.vkCode;
        if (vk is < 0 or > 255) return CallNextHookEx(_hook, nCode, wParam, lParam);

        try
        {
            if (_capture != null) { if (HandleCapture(vk, isDown, isUp)) return (IntPtr)1; }
            else if (Enabled && Handle(vk, isDown, isUp)) return (IntPtr)1; // swallow
        }
        catch (Exception ex) { Log.Error("hook", ex); }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>Test entry: one key event as the hook would see it. Returns true when Vox would swallow it.</summary>
    internal bool Feed(int vk, bool isDown) => _capture != null ? HandleCapture(vk, isDown, !isDown) : Handle(vk, isDown, !isDown);

    static readonly int[] Modifiers = { VK_LSHIFT, VK_RSHIFT, VK_LCONTROL, VK_RCONTROL, VK_LMENU, VK_RMENU, VK_LWIN, VK_RWIN };

    /// <summary>Returns true when the key event must be swallowed.</summary>
    bool Handle(int vk, bool isDown, bool isUp)
    {
        // Resync modifier state from the OS (a missed key-up must never leave the chord stuck).
        foreach (var m in Modifiers) if (m != vk) _down[m] = OsKeyDown(m);
        bool wasDown = _down[vk];
        if (isDown) _down[vk] = true; else if (isUp) _down[vk] = false;
        bool Down(int v) => _down[v];

        var dict = Dictation;

        // A key Vox used on its way down is swallowed on its way up too (so the app never sees half a keystroke).
        if (isUp && _swallowUntilUp.Remove(vk))
        {
            UpdateChord(dict);
            return true;
        }

        // ----- Space: "dictation keys + Space" switches to hands-free -----
        if (vk == VK_SPACE && !dict.Keys.Contains(VK_SPACE))
        {
            if (isDown && dict.IsHeld(v => v != VK_SPACE && Down(v)))
            {
                if (!wasDown) OnChordSpace();
                _spaceSwallowed = true;
                return true;
            }
            if (isUp && _spaceSwallowed) { _spaceSwallowed = false; return true; }
        }

        // ----- Esc cancels an active dictation -----
        if (vk == VK_ESCAPE)
        {
            if (isDown && _state != State.Idle)
            {
                _state = State.Idle; _ignoreRelease = _chordDown;
                Post(() => DictationCancelled?.Invoke(false));
                _escSwallowed = true;
                return true;
            }
            if (isUp && _escSwallowed) { _escSwallowed = false; return true; }
        }

        // ----- Paste / copy last transcript (fire on the shortcut's last key going down) -----
        if (isDown && !KeyCombo.IsModifierKey(vk))
        {
            foreach (var (combo, act) in new (KeyCombo, Action)[] { (PasteLast, () => PasteLastRequested?.Invoke()), (CopyLast, () => CopyLastRequested?.Invoke()) })
            {
                if (combo.IsModifierOnly || !combo.Keys.Contains(vk) || combo.Equals(dict) || !combo.IsHeld(Down)) continue;
                if (!wasDown) Post(act);
                _swallowUntilUp.Add(vk);
                return true;
            }
        }

        // ----- The dictation shortcut itself -----
        UpdateChord(dict);

        // Non-modifier keys of the dictation shortcut (F9, Space in Ctrl+Shift+Space, ...) must not reach the app.
        if (isDown && dict.Keys.Contains(vk) && dict.ModifiersMatch(Down)) { _swallowUntilUp.Add(vk); return true; }
        return false;
    }

    void UpdateChord(KeyCombo dict)
    {
        bool chord = dict.IsHeld(v => _down[v]);
        if (chord && !_chordDown) { _chordDown = true; OnChordDown(dict); }
        else if (!chord && _chordDown) { _chordDown = false; OnChordUp(); }
    }

    void OnChordDown(KeyCombo dict)
    {
        _chordDownAt = DateTime.UtcNow;
        if (dict.Win && !TestMode) MaskStartMenu();
        switch (_state)
        {
            case State.HandsFree:
                _state = State.Idle; _ignoreRelease = true;
                Post(() => DictationFinished?.Invoke());
                break;
            case State.Idle:
                if ((DateTime.UtcNow - _lastTapAt).TotalMilliseconds < DoubleTapMs)
                {
                    _state = State.HandsFree; _ignoreRelease = true; _lastTapAt = DateTime.MinValue;
                    Post(() => DictationStarted?.Invoke(DictationMode.HandsFree));
                }
                else
                {
                    _state = State.Ptt;
                    Post(() => DictationStarted?.Invoke(DictationMode.PushToTalk));
                }
                break;
        }
    }

    void OnChordUp()
    {
        if (_ignoreRelease) { _ignoreRelease = false; return; }
        if (_state != State.Ptt) return;
        var held = (DateTime.UtcNow - _chordDownAt).TotalMilliseconds;
        _state = State.Idle;
        if (held < TapMs)
        {
            _lastTapAt = DateTime.UtcNow;                     // might be the first half of a double-tap
            Post(() => DictationCancelled?.Invoke(true));
        }
        else Post(() => DictationFinished?.Invoke());
    }

    void OnChordSpace()
    {
        switch (_state)
        {
            case State.Ptt:
                _state = State.HandsFree; _ignoreRelease = true;      // keep recording after the keys are let go
                Post(() => DictationStarted?.Invoke(DictationMode.HandsFree));
                break;
            case State.HandsFree:
                _state = State.Idle; _ignoreRelease = true;
                Post(() => DictationFinished?.Invoke());
                break;
        }
    }

    /// <summary>Releasing the Win key alone opens Start; an injected unassigned key in between prevents it.</summary>
    static void MaskStartMenu()
    {
        try { Send(Key((ushort)VK_MASK, false), Key((ushort)VK_MASK, true)); } catch { }
    }

    void Post(Action a) => _ui.Post(_ => { try { a(); } catch (Exception ex) { Log.Error("hotkey handler", ex); } }, null);
}
