using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using Microsoft.Win32;
using Vox.Core;
using Vox.UI;
using WinForms = System.Windows.Forms;

namespace Vox;

/// <summary>Everything the UI and tray need, created once at startup.</summary>
public sealed class Services
{
    public AppSettings Settings = null!;
    public DataStore Data = null!;
    public SpeechEngine Engine = new();
    /// <summary>Second engine for per-app accurate mode and "re-hear": loaded the first time it's needed, then kept.</summary>
    public SpeechEngine AccurateEngine = new();
    Task<SpeechEngine?>? _accurateLoad;
    public DictationController Dictation = null!;

    /// <summary>The accurate-mode model (.gguf) in the app or data folder, if one is installed. Looked up once and again
    /// whenever the model list may have changed (RefreshAccurateModel), not on every dictation.</summary>
    public FileInfo? AccurateModel { get; private set; }
    public void RefreshAccurateModel() => AccurateModel = QwenEngine.IsAvailable
        ? Data.FindModels(AppContext.BaseDirectory).Where(f => QwenEngine.IsGgufModel(f.Name)).OrderBy(f => f.Length).FirstOrDefault() : null;

    public Task<SpeechEngine?> GetAccurateEngineAsync()
    {
        if (Engine.WantsWholeClip) return Task.FromResult<SpeechEngine?>(Engine);   // the main model already is accurate mode
        lock (this)
        {
            if (_accurateLoad is { IsFaulted: false }) return _accurateLoad;
            var m = AccurateModel;
            if (m == null) return Task.FromResult<SpeechEngine?>(null);
            _accurateLoad = Task.Run(async () => { await AccurateEngine.LoadAsync(m.FullName); return (SpeechEngine?)AccurateEngine; });
            return _accurateLoad;
        }
    }
    public MeetingRecorder Meeting = null!;
    public HotkeyService Hotkeys = null!;
    public VoxBar Bar = null!;
    public bool ModelLoading, Quitting;
    public event Action? ModelChanged;
    public event Action? PrivateModeChanged;

    public void SetPrivateMode(bool on)
    {
        if (Settings.PrivateMode == on) return;
        Settings.PrivateMode = on; Settings.Save();
        Log.Info($"private mode {(on ? "on" : "off")}");
        PrivateModeChanged?.Invoke();
    }
    public Action? OnMeetingChanged;

    public bool AnyModelAvailable => Data.FindModels(AppContext.BaseDirectory).Any();

    public FileInfo? PickModel()
    {
        var models = Data.FindModels(AppContext.BaseDirectory).ToList();
        if (models.Count == 0) return null;
        var chosen = models.FirstOrDefault(m => m.Name.Equals(Settings.ModelFile, StringComparison.OrdinalIgnoreCase));
        if (chosen != null) return chosen;
        // sensible default: Parakeet (fast and accurate on CPU) if present, else a "small" model, else "base", else the largest
        return (ParakeetEngine.IsAvailable ? models.FirstOrDefault(m => SpeechEngine.IsParakeetModel(m.Name)) : null)
            ?? models.FirstOrDefault(m => m.Name.Contains("small", StringComparison.OrdinalIgnoreCase))
            ?? models.FirstOrDefault(m => m.Name.Contains("base", StringComparison.OrdinalIgnoreCase))
            ?? models.Last();
    }

    public async Task LoadModelAsync()
    {
        RefreshAccurateModel();   // the model list may have changed (startup, import, new choice)
        var m = PickModel();
        if (m == null) { Log.Info("no speech model found in the app or data folder; waiting for the user to add one"); ModelChanged?.Invoke(); return; }
        ModelLoading = true; ModelChanged?.Invoke();
        try
        {
            Settings.ModelFile = m.Name; Settings.Save();
            await Engine.LoadAsync(m.FullName);
        }
        catch (Exception ex) { Log.Error("model load", ex); System.Windows.MessageBox.Show("Could not load the speech model:\n\n" + ex.Message, "Vox"); }
        finally { ModelLoading = false; ModelChanged?.Invoke(); }
    }

    public void ApplySettings(bool modelChanged)
    {
        Hotkeys.Dictation = Settings.DictationCombo;
        Hotkeys.PasteLast = Settings.PasteLastCombo;
        Hotkeys.CopyLast = Settings.CopyLastCombo;
        Hotkeys.Rehear = Settings.RehearCombo;
        Bar.AlwaysVisible = Settings.ShowVoxBarAlways;
        Bar.SetState(Dictation.State, "");
        SetAutostart(Settings.LaunchAtLogin);
        if (modelChanged) _ = LoadModelAsync();
    }

    public void NotifyMeeting(bool running) => OnMeetingChanged?.Invoke();

    static void SetAutostart(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            if (on) key.SetValue("Vox", "\"" + Environment.ProcessPath + "\" --background");
            else key.DeleteValue("Vox", false);
        }
        catch (Exception ex) { Log.Error("autostart", ex); }
    }

    public void Restart()
    {
        Quitting = true;
        try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true }); } catch { }
        System.Windows.Application.Current.Shutdown();
    }
}

public partial class App : System.Windows.Application
{
    Mutex? _mutex;
    EventWaitHandle? _showEvent;
    Services _svc = null!;
    MainWindow? _main;
    WinForms.NotifyIcon? _tray;
    WinForms.ToolStripMenuItem? _pauseItem;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += (_, a) => { Log.Error("unhandled", a.Exception); a.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Error("fatal", a.ExceptionObject as Exception);

        // accurate-mode helper: Vox starts its own copy with this to run Qwen3 (see Core\QwenEngine.cs); no window, no tray
        if (e.Args.Length >= 4 && e.Args[0] == "--asr-host") { await Task.Run(() => QwenEngine.RunHost(e.Args)); Shutdown(); return; }
        if (e.Args.Length >= 3 && e.Args[0] == "--selftest") { await SelfTest.RunAsync(e.Args); Shutdown(); return; }
        if (e.Args.Length >= 1 && e.Args[0] == "--sysinfo")
        {
            var text = SysInfo.Build();
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "vox-sysinfo.txt"), text); } catch { }
            try { TextInjector.SetClipboard(text); } catch { }
            MessageBox.Show(text + "\n(Copied to the clipboard.)", "Vox system check", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(); return;
        }
        if (e.Args.Length >= 4 && e.Args[0] == "--selftest-live") { await SelfTest.RunLiveAsync(e.Args); Shutdown(); return; }
        if (e.Args.Length >= 4 && e.Args[0] == "--train") { await Trainer.TrainAsync(e.Args); Shutdown(); return; }
        if (e.Args.Length >= 4 && e.Args[0] == "--mic-test") { await Trainer.MicTestAsync(e.Args); Shutdown(); return; }
        if (e.Args.Length >= 3 && e.Args[0] == "--make-track") { Trainer.MakeTrack(e.Args); Shutdown(); return; }
        if (e.Args.Length >= 3 && e.Args[0] == "--mic-listen") { await Trainer.MicListenAsync(e.Args); Shutdown(); return; }
        if (e.Args.Length >= 3 && e.Args[0] == "--noise-test") { await Trainer.NoiseTestAsync(e.Args); Shutdown(); return; }
        if (e.Args.Length >= 4 && e.Args[0] == "--gate-test") { await Trainer.GateTestAsync(e.Args); Shutdown(); return; }
        if (e.Args.Length >= 2 && e.Args[0] == "--dict-check") { Trainer.DictCheck(e.Args); Shutdown(); return; }
        if (e.Args.Length >= 3 && e.Args[0] == "--clean")   // --clean outFile inFile: one line in, cleaned line out (rule checks)
        { Trainer.CleanFile(e.Args); Shutdown(); return; }
        if (e.Args.Length >= 5 && e.Args[0] == "--real-test") { try { await Trainer.RealTestAsync(e.Args); } catch (Exception ex) { File.WriteAllText(Path.Combine(e.Args[1], "real-error.txt"), ex.ToString()); } Shutdown(); return; }
        if (e.Args.Length >= 5 && e.Args[0] == "--soak") { await Trainer.SoakAsync(e.Args); Shutdown(); return; }
        if (e.Args.Length >= 4 && e.Args[0] == "--level-test") { try { Trainer.LevelTest(e.Args); } catch (Exception ex) { File.WriteAllText(e.Args[1] + ".error.txt", ex.ToString()); } Shutdown(); return; }
        if (e.Args.Length >= 4 && e.Args[0] == "--score") { try { Trainer.ScoreFiles(e.Args); } catch (Exception ex) { File.WriteAllText(e.Args[1] + ".error.txt", ex.ToString()); } Shutdown(); return; }
        if (e.Args.Length >= 6 && e.Args[0] == "--chunk-speed") { await Trainer.ChunkSpeedAsync(e.Args); Shutdown(); return; }
        if (e.Args.Length >= 2 && e.Args[0] == "--feature-tests") { try { Trainer.FeatureTests(e.Args); } catch (Exception ex) { File.WriteAllText(e.Args[1] + ".error.txt", ex.ToString()); } Shutdown(); return; }
        if (e.Args.Length >= 6 && e.Args[0] == "--model-bench") { try { await Trainer.ModelBenchAsync(e.Args); } catch (Exception ex) { File.WriteAllText(Path.Combine(e.Args[1], "error.txt"), ex.ToString()); } Shutdown(); return; }
        if (e.Args.Length >= 5 && e.Args[0] == "--live-replay") { try { await Trainer.LiveReplayAsync(e.Args); } catch (Exception ex) { File.WriteAllText(e.Args[1] + ".error.txt", ex.ToString()); } Shutdown(); return; }
        if (e.Args.Length >= 4 && e.Args[0] == "--qa-audio") { await Trainer.QaAudioAsync(e.Args); Shutdown(); return; }
        if (e.Args.Length >= 3 && e.Args[0] == "--rule-tests") { try { Trainer.RuleTests(e.Args); } catch (Exception ex) { File.WriteAllText(e.Args[1] + ".error.txt", ex.ToString()); } Shutdown(); return; }
        if (e.Args.Length >= 3 && e.Args[0] == "--analyze") { try { Trainer.Analyze(e.Args); } catch (Exception ex) { File.WriteAllText(Path.Combine(e.Args[2], "analyze-error.txt"), ex.ToString()); } Shutdown(); return; }
        if (e.Args.Length >= 2 && e.Args[0] == "--learn") { try { Trainer.Learn(e.Args[1], e.Args.Length > 2 ? e.Args[2] : null); } catch (Exception ex) { File.WriteAllText(Path.Combine(e.Args[1], "learn-error.txt"), ex.ToString()); } Shutdown(); return; }
        if (e.Args.Length >= 2 && e.Args[0] == "--make-icon") { IconMaker.Write(e.Args[1]); if (e.Args.Length > 2) IconMaker.Preview(e.Args[2]); Shutdown(); return; }
        if (e.Args.Length >= 2 && e.Args[0] == "--hotkey-test") { HotkeyTest.Run(e.Args); Shutdown(); return; }
        if (e.Args.Length >= 3 && e.Args[0] == "--loopback-test") { await MeetingTest.LoopbackAsync(e.Args); Shutdown(); return; }
        if (e.Args.Length >= 3 && e.Args[0] == "--spk-probe") { MeetingTest.Probe(e.Args); Shutdown(); return; }
        if (e.Args.Length >= 4 && e.Args[0] == "--meeting-test") { await MeetingTest.RunAsync(e.Args); Shutdown(); return; }
        if (e.Args.Length >= 1 && e.Args[0] == "--demo-bar") { await DemoVoxBarAsync(e.Args.Length > 1 ? e.Args[1] : null); Shutdown(); return; }

        // single instance: a second launch just brings up the first one
        // a test copy with its own settings folder (VOX_SETTINGS_DIR) may run beside the real Vox
        var inst = Environment.GetEnvironmentVariable("VOX_SETTINGS_DIR") is { Length: > 0 } td ? "7f3a." + System.Text.RegularExpressions.Regex.Replace(td.ToLowerInvariant(), "[^a-z0-9]", "_") : "7f3a";
        _mutex = new Mutex(true, "Vox.SingleInstance." + inst, out bool first);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Vox.Show." + inst);
        if (!first) { _showEvent.Set(); Shutdown(); return; }
        new Thread(() => { while (_showEvent.WaitOne()) Dispatcher.BeginInvoke(ShowMain); }) { IsBackground = true }.Start();

        var s = new Services { Settings = AppSettings.Load() };
        _svc = s;

        // first run: let the user pick where everything is saved
        bool firstRun = string.IsNullOrWhiteSpace(s.Settings.DataFolder) || !Directory.Exists(s.Settings.DataFolder) && !CanCreate(s.Settings.DataFolder);
        if (firstRun)
        {
            // Documents is often synced to OneDrive; dictation audio and transcripts must stay on this PC, so default to the profile folder then
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var def = Path.Combine(docs.Contains("OneDrive", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(docs)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : docs, "Vox");
            System.Windows.MessageBox.Show("Welcome to Vox.\n\nVox works completely offline. Everything it records or writes (history, meeting transcripts, dictionary) is saved in a folder on this PC.\n\nNext, choose that folder.", "Vox setup", MessageBoxButton.OK, MessageBoxImage.Information);
            using var dlg = new WinForms.FolderBrowserDialog { Description = "Choose where Vox saves its data", SelectedPath = def, UseDescriptionForTitle = true, ShowNewFolderButton = true };
            s.Settings.DataFolder = dlg.ShowDialog() == WinForms.DialogResult.OK ? dlg.SelectedPath : def;
            s.Settings.Save();
        }

        try { s.Data = new DataStore(s.Settings.DataFolder); }
        catch (Exception ex)
        {
            Log.Error("data folder", ex);
            System.Windows.MessageBox.Show("Vox cannot use the data folder:\n" + s.Settings.DataFolder + "\n\n" + ex.Message, "Vox");
            Shutdown(); return;
        }

        var problems = s.Data.LoadProblems.Prepend(AppSettings.LoadProblem).Where(p => p != null).ToList();
        if (problems.Count > 0)
            System.Windows.MessageBox.Show(string.Join("\n\n", problems), "Vox", MessageBoxButton.OK, MessageBoxImage.Warning);

        s.Bar = new VoxBar { AlwaysVisible = s.Settings.ShowVoxBarAlways };
        s.Dictation = new DictationController(s.Settings, s.Data, s.Engine);
        s.Meeting = new MeetingRecorder(s.Settings, s.Data, s.Engine);
        s.Hotkeys = new HotkeyService(System.Threading.SynchronizationContext.Current!) { Dictation = s.Settings.DictationCombo, PasteLast = s.Settings.PasteLastCombo, CopyLast = s.Settings.CopyLastCombo, Rehear = s.Settings.RehearCombo };
        s.Dictation.GetAccurateEngine = s.GetAccurateEngineAsync;
        s.Dictation.AccurateAvailable = () => s.AccurateModel != null;

        s.Dictation.StateChanged += (st, msg) =>
        {
            s.Bar.SetState(st, msg);
            if (st != VoxState.Listening) s.Hotkeys.ResetState();
            if (_tray != null) _tray.Text = st == VoxState.Listening ? "Vox - listening" : s.Settings.PrivateMode ? "Vox (private mode)" : "Vox";
        };
        s.Dictation.Level += l => s.Bar.Dispatcher.BeginInvoke(() => s.Bar.PushLevel(l));
        s.Hotkeys.DictationStarted += m => s.Dictation.Start(m);
        s.Hotkeys.DictationFinished += () => _ = s.Dictation.FinishAsync();
        s.Hotkeys.DictationCancelled += silent => s.Dictation.Cancel(silent);
        s.Hotkeys.PasteLastRequested += () => _ = s.Dictation.PasteLastAsync();
        s.Hotkeys.CopyLastRequested += () => s.Dictation.CopyLast();
        s.Hotkeys.RehearRequested += () => _ = s.Dictation.RehearLastAsync();

        _main = new MainWindow(s);
        BuildTray(s);
        s.Bar.Show();
        s.Bar.SetState(VoxState.Idle, "");
        s.Hotkeys.Start();
        s.ApplySettings(false);

        bool background = e.Args.Contains("--background");
        if (!background) _main.Show();

        await s.LoadModelAsync();
        if (!s.Engine.IsLoaded && !background) ShowMain();
    }

    /// <summary>Developer aid: cycles the Vox bar through its states with synthetic levels (no mic, no model).</summary>
    /// <summary>Developer aid: Vox.exe --demo-bar [pngFolder] cycles the Vox bar through every state; with a folder it also saves a picture of each.</summary>
    static async Task DemoVoxBarAsync(string? snapDir = null)
    {
        var bar = new VoxBar { AlwaysVisible = true };
        var rnd = new Random(1);
        async Task Talk(int frames) { for (int i = 0; i < frames; i++) { bar.PushLevel((float)(0.002 + 0.03 * Math.Abs(Math.Sin(i / 7.0)) * rnd.NextDouble())); await Task.Delay(25); } }
        void Snap(string name)
        {
            if (snapDir == null || bar.Content is not FrameworkElement root) return;
            Directory.CreateDirectory(snapDir);
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(root.ActualWidth * 2), (int)(root.ActualHeight * 2), 192, 192, System.Windows.Media.PixelFormats.Pbgra32);
            bmp.Render(root);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder(); enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using var f = File.Create(Path.Combine(snapDir, name + ".png")); enc.Save(f);
        }
        bar.Show(); bar.SetState(VoxState.Idle, ""); await Task.Delay(1500); Snap("1-resting");
        bar.SetState(VoxState.Listening, "Listening"); await Talk(100); Snap("2-listening");
        bar.SetState(VoxState.Listening, "Hands-free"); await Talk(100); Snap("3-hands-free");
        bar.SetState(VoxState.Listening, DictationController.QuietLabel); await Talk(80); Snap("4-quiet-mic");
        bar.SetState(VoxState.Processing, "Transcribing"); await Task.Delay(1500); Snap("5-writing");
        bar.SetState(VoxState.Done, "1.2s"); await Task.Delay(1500); Snap("6-written");
        bar.SetState(VoxState.Notice, "No sound heard. Check your microphone."); await Task.Delay(1500); Snap("7-notice");
        bar.SetState(VoxState.Idle, ""); await Task.Delay(1000);
        bar.Close();
    }

    static bool CanCreate(string path) { try { Directory.CreateDirectory(path); return true; } catch { return false; } }

    void ShowMain()
    {
        if (_main == null) return;
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    void BuildTray(Services s)
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open Vox", null, (_, _) => ShowMain());
        menu.Items.Add("Meetings...", null, (_, _) => { ShowMain(); _main!.ShowMeetingsTab(); });
        menu.Items.Add(new WinForms.ToolStripSeparator());
        _pauseItem = new WinForms.ToolStripMenuItem("Pause shortcut") { CheckOnClick = true };
        _pauseItem.CheckedChanged += (_, _) => s.Hotkeys.Enabled = !_pauseItem.Checked;
        menu.Items.Add(_pauseItem);
        // private mode: dictations still type, but nothing is saved to history or audio (for anything sensitive)
        var privateItem = new WinForms.ToolStripMenuItem("Private mode (save no history)") { CheckOnClick = true, Checked = s.Settings.PrivateMode };
        privateItem.CheckedChanged += (_, _) => s.SetPrivateMode(privateItem.Checked);
        s.PrivateModeChanged += () =>
        {
            privateItem.Checked = s.Settings.PrivateMode;
            if (_tray != null) _tray.Text = s.Settings.PrivateMode ? "Vox (private mode)" : "Vox";
        };
        menu.Items.Add(privateItem);
        menu.Items.Add("Open notes", null, (_, _) =>
        {
            var notes = Path.Combine(s.Data.Root, "Notes.md");
            if (!File.Exists(notes)) File.WriteAllText(notes, "# Notes to self\n\nSay \"note to self\" and then your note.\n\n");
            Process.Start(new ProcessStartInfo(notes) { UseShellExecute = true });
        });
        menu.Items.Add("Open data folder", null, (_, _) => Process.Start(new ProcessStartInfo(s.Data.Root) { UseShellExecute = true }));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Quit Vox", null, async (_, _) => await QuitAsync());

        _tray = new WinForms.NotifyIcon { Icon = IconMaker.Make(32), Visible = true, Text = s.Settings.PrivateMode ? "Vox (private mode)" : "Vox", ContextMenuStrip = menu };
        _tray.DoubleClick += (_, _) => ShowMain();
    }

    async Task QuitAsync()
    {
        _svc.Quitting = true;
        if (_svc.Meeting.IsRunning) await _main!.StopMeetingAsync();
        _svc.Hotkeys.Dispose();
        _tray!.Visible = false; _tray.Dispose();
        _svc.Engine.Dispose();
        _svc.AccurateEngine.Dispose();
        Shutdown();
    }
}

internal static class IconMaker
{
    static readonly System.Drawing.Color Ink = System.Drawing.Color.FromArgb(0x1F, 0x3A, 0x5F), PaperC = System.Drawing.Color.FromArgb(0xF7, 0xF4, 0xEB),
        Grid = System.Drawing.Color.FromArgb(0x22, 0x1F, 0x3A, 0x5F), Margin = System.Drawing.Color.FromArgb(0xB4, 0x43, 0x2F);

    // the talk button's ink line (same shape as the main window), in a 56 x 34 box; small sizes use fewer, bolder strokes
    static readonly PointF[] WaveFull = { new(2, 17), new(8, 14), new(12, 22), new(17, 7), new(22, 27), new(27, 10), new(32, 21), new(37, 4), new(42, 28), new(47, 13), new(52, 18), new(54, 17) };
    static readonly PointF[] WaveSmall = { new(2, 17), new(12, 10), new(20, 26), new(28, 4), new(36, 30), new(44, 11), new(54, 17) };

    /// <summary>
    /// Draws the Vox mark in the notebook look: a sheet of graph paper with an ink rim and a red margin line, and the ink
    /// talk button with its waveform in the middle. Below 40 px the grid and margin are left out so it stays crisp.
    /// </summary>
    public static Bitmap Draw(int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(System.Drawing.Color.Transparent);
        bool small = size < 40;

        float rim = Math.Max(1f, size * (small ? 0.045f : 0.034f)), inset = rim / 2 + (small ? 0.5f : size * 0.02f);
        var tile = new RectangleF(inset, inset, size - 2 * inset, size - 2 * inset);
        using var tilePath = Rounded(tile, size * 0.2f);
        using (var pb = new SolidBrush(PaperC)) g.FillPath(pb, tilePath);
        if (!small)
        {
            g.SetClip(tilePath);
            float step = size / 8f;
            using var gp = new Pen(Grid, Math.Max(1f, size / 128f));
            for (float v = step; v < size; v += step) { g.DrawLine(gp, v, 0, v, size); g.DrawLine(gp, 0, v, size, v); }
            using var mp = new Pen(Margin, Math.Max(1.5f, size * 0.022f));
            g.DrawLine(mp, size * 0.2f, 0, size * 0.2f, size);
            g.ResetClip();
        }
        using (var rp = new Pen(Ink, rim)) g.DrawPath(rp, tilePath);

        // the ink talk button
        float d = size * (small ? 0.68f : 0.58f), cx = size / 2f, cy = size / 2f;
        using (var ib = new SolidBrush(Ink)) g.FillEllipse(ib, cx - d / 2, cy - d / 2, d, d);

        var src = small ? WaveSmall : WaveFull;
        float w = d * 0.66f, h = w * 34f / 56f;
        var pts = src.Select(p => new PointF(cx - w / 2 + p.X / 56f * w, cy - h / 2 + p.Y / 34f * h)).ToArray();
        using var wp = new Pen(PaperC, Math.Max(1.4f, size * (small ? 0.075f : 0.045f))) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLines(wp, pts);
        return bmp;
    }

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>A strip of the mark at every icon size, on paper and on a dark taskbar color, for checking by eye.</summary>
    public static void Preview(string pngPath)
    {
        var sizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
        int pad = 16, W = sizes.Sum() + pad * (sizes.Length + 1);
        using var sheet = new Bitmap(W, (256 + pad * 2) * 2);
        using var g = Graphics.FromImage(sheet);
        g.Clear(PaperC);
        using (var dark = new SolidBrush(System.Drawing.Color.FromArgb(0x20, 0x20, 0x20))) g.FillRectangle(dark, 0, 256 + pad * 2, W, 256 + pad * 2);
        for (int row = 0; row < 2; row++)
        {
            int x = pad;
            foreach (var s in sizes) { using var b = Draw(s); g.DrawImage(b, x, row * (256 + pad * 2) + pad + 256 - s); x += s + pad; }
        }
        sheet.Save(pngPath, System.Drawing.Imaging.ImageFormat.Png);
    }

    public static Icon Make(int size) { using var b = Draw(size); return Icon.FromHandle(b.GetHicon()); }

    /// <summary>Writes a multi-size .ico (PNG-compressed entries) for the exe and installer.</summary>
    public static void Write(string path)
    {
        var sizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
        var pngs = sizes.Select(sz => { using var b = Draw(sz); using var ms = new MemoryStream(); b.Save(ms, System.Drawing.Imaging.ImageFormat.Png); return ms.ToArray(); }).ToList();
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            w.Write((byte)(sizes[i] == 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
            w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
            w.Write(pngs[i].Length); w.Write(offset); offset += pngs[i].Length;
        }
        foreach (var p in pngs) w.Write(p);
    }
}
