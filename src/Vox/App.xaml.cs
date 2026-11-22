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
    public DictationController Dictation = null!;
    public MeetingRecorder Meeting = null!;
    public HotkeyService Hotkeys = null!;
    public FlowBar Bar = null!;
    public bool ModelLoading, Quitting;
    public event Action? ModelChanged;
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
        Bar.AlwaysVisible = Settings.ShowFlowBarAlways;
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
        if (e.Args.Length >= 6 && e.Args[0] == "--model-bench") { try { await Trainer.ModelBenchAsync(e.Args); } catch (Exception ex) { File.WriteAllText(Path.Combine(e.Args[1], "error.txt"), ex.ToString()); } Shutdown(); return; }
        if (e.Args.Length >= 5 && e.Args[0] == "--live-replay") { try { await Trainer.LiveReplayAsync(e.Args); } catch (Exception ex) { File.WriteAllText(e.Args[1] + ".error.txt", ex.ToString()); } Shutdown(); return; }
        if (e.Args.Length >= 4 && e.Args[0] == "--qa-audio") { await Trainer.QaAudioAsync(e.Args); Shutdown(); return; }
        if (e.Args.Length >= 3 && e.Args[0] == "--rule-tests") { try { Trainer.RuleTests(e.Args); } catch (Exception ex) { File.WriteAllText(e.Args[1] + ".error.txt", ex.ToString()); } Shutdown(); return; }
        if (e.Args.Length >= 3 && e.Args[0] == "--analyze") { try { Trainer.Analyze(e.Args); } catch (Exception ex) { File.WriteAllText(Path.Combine(e.Args[2], "analyze-error.txt"), ex.ToString()); } Shutdown(); return; }
        if (e.Args.Length >= 2 && e.Args[0] == "--learn") { try { Trainer.Learn(e.Args[1], e.Args.Length > 2 ? e.Args[2] : null); } catch (Exception ex) { File.WriteAllText(Path.Combine(e.Args[1], "learn-error.txt"), ex.ToString()); } Shutdown(); return; }
        if (e.Args.Length >= 2 && e.Args[0] == "--make-icon") { IconMaker.Write(e.Args[1]); Shutdown(); return; }
        if (e.Args.Length >= 2 && e.Args[0] == "--hotkey-test") { HotkeyTest.Run(e.Args); Shutdown(); return; }
        if (e.Args.Length >= 3 && e.Args[0] == "--loopback-test") { await MeetingTest.LoopbackAsync(e.Args); Shutdown(); return; }
        if (e.Args.Length >= 3 && e.Args[0] == "--spk-probe") { MeetingTest.Probe(e.Args); Shutdown(); return; }
        if (e.Args.Length >= 4 && e.Args[0] == "--meeting-test") { await MeetingTest.RunAsync(e.Args); Shutdown(); return; }
        if (e.Args.Length >= 1 && e.Args[0] == "--demo-flowbar") { await DemoFlowBarAsync(); Shutdown(); return; }

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

        s.Bar = new FlowBar { AlwaysVisible = s.Settings.ShowFlowBarAlways };
        s.Dictation = new DictationController(s.Settings, s.Data, s.Engine);
        s.Meeting = new MeetingRecorder(s.Settings, s.Data, s.Engine);
        s.Hotkeys = new HotkeyService(System.Threading.SynchronizationContext.Current!) { Dictation = s.Settings.DictationCombo, PasteLast = s.Settings.PasteLastCombo, CopyLast = s.Settings.CopyLastCombo };

        s.Dictation.StateChanged += (st, msg) =>
        {
            s.Bar.SetState(st, msg);
            if (st != VoxState.Listening) s.Hotkeys.ResetState();
            if (_tray != null) _tray.Text = st == VoxState.Listening ? "Vox - listening" : "Vox";
        };
        s.Dictation.Level += l => s.Bar.Dispatcher.BeginInvoke(() => s.Bar.PushLevel(l));
        s.Hotkeys.DictationStarted += m => s.Dictation.Start(m);
        s.Hotkeys.DictationFinished += () => _ = s.Dictation.FinishAsync();
        s.Hotkeys.DictationCancelled += silent => s.Dictation.Cancel(silent);
        s.Hotkeys.PasteLastRequested += () => _ = s.Dictation.PasteLastAsync();
        s.Hotkeys.CopyLastRequested += () => s.Dictation.CopyLast();

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

    /// <summary>Developer aid: cycles the Flow Bar through its states with synthetic levels (no mic, no model).</summary>
    static async Task DemoFlowBarAsync()
    {
        var bar = new FlowBar { AlwaysVisible = true };
        bar.Show(); bar.SetState(VoxState.Idle, "");
        await Task.Delay(4000);
        bar.SetState(VoxState.Listening, "Listening");
        var rnd = new Random(1);
        for (int i = 0; i < 240; i++) { bar.PushLevel((float)(0.002 + 0.03 * Math.Abs(Math.Sin(i / 7.0)) * rnd.NextDouble())); await Task.Delay(25); }
        bar.SetState(VoxState.Processing, "Transcribing"); await Task.Delay(4000);
        bar.SetState(VoxState.Done, "1.2s"); await Task.Delay(3000);
        bar.SetState(VoxState.Notice, "No sound heard. Check your microphone."); await Task.Delay(3000);
        bar.SetState(VoxState.Idle, ""); await Task.Delay(1500);
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
        menu.Items.Add("Open data folder", null, (_, _) => Process.Start(new ProcessStartInfo(s.Data.Root) { UseShellExecute = true }));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Quit Vox", null, async (_, _) => await QuitAsync());

        _tray = new WinForms.NotifyIcon { Icon = IconMaker.Make(32), Visible = true, Text = "Vox", ContextMenuStrip = menu };
        _tray.DoubleClick += (_, _) => ShowMain();
    }

    async Task QuitAsync()
    {
        _svc.Quitting = true;
        if (_svc.Meeting.IsRunning) await _main!.StopMeetingAsync();
        _svc.Hotkeys.Dispose();
        _tray!.Visible = false; _tray.Dispose();
        _svc.Engine.Dispose();
        Shutdown();
    }
}

internal static class IconMaker
{
    /// <summary>Draws the Vox mark: teal rounded square with five waveform bars.</summary>
    public static Bitmap Draw(int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(System.Drawing.Color.Transparent);
        float r = size * 0.22f;
        using var path = new GraphicsPath();
        path.AddArc(0, 0, r * 2, r * 2, 180, 90); path.AddArc(size - r * 2, 0, r * 2, r * 2, 270, 90);
        path.AddArc(size - r * 2, size - r * 2, r * 2, r * 2, 0, 90); path.AddArc(0, size - r * 2, r * 2, r * 2, 90, 90);
        path.CloseFigure();
        using (var br = new LinearGradientBrush(new System.Drawing.Rectangle(0, 0, size, size), System.Drawing.Color.FromArgb(0x14, 0x9A, 0xA6), System.Drawing.Color.FromArgb(0x0A, 0x5C, 0x66), 60f))
            g.FillPath(br, path);
        float[] hs = { 0.30f, 0.55f, 0.80f, 0.55f, 0.30f };
        float bw = size * 0.085f, gap = size * 0.065f, total = 5 * bw + 4 * gap, x = (size - total) / 2;
        using var wb = new SolidBrush(System.Drawing.Color.White);
        for (int i = 0; i < 5; i++)
        {
            float h = size * hs[i];
            var rect = new RectangleF(x + i * (bw + gap), (size - h) / 2, bw, h);
            using var bp = new GraphicsPath();
            float rr = bw / 2;
            bp.AddArc(rect.X, rect.Y, bw, bw, 180, 180); bp.AddArc(rect.X, rect.Bottom - bw, bw, bw, 0, 180); bp.CloseFigure();
            g.FillPath(wb, bp);
        }
        return bmp;
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
