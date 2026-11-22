using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Vox.Core;
using WinForms = System.Windows.Forms;

namespace Vox;

public partial class MainWindow : Window
{
    readonly Services S;
    bool _loading = true;
    readonly DispatcherTimer _meetingTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    static readonly (string Code, string Name)[] Languages =
    {
        ("auto", "Auto-detect"), ("en", "English"), ("es", "Spanish"), ("fr", "French"), ("de", "German"), ("pt", "Portuguese"),
        ("it", "Italian"), ("nl", "Dutch"), ("pl", "Polish"), ("ru", "Russian"), ("uk", "Ukrainian"), ("tr", "Turkish"),
        ("zh", "Chinese"), ("ja", "Japanese"), ("ko", "Korean"), ("hi", "Hindi"), ("ar", "Arabic"),
    };

    public MainWindow(Services services)
    {
        InitializeComponent();
        S = services;
        HistoryList.ItemsSource = S.Data.History;
        DictGrid.ItemsSource = S.Data.Dictionary;
        SnipGrid.ItemsSource = S.Data.Snippets;
        S.Data.History.CollectionChanged += (_, _) => UpdateHistoryCount();
        UpdateHistoryCount();

        PopulateSettings();
        RefreshMeetingFiles();
        RefreshStatus();
        VersionText.Text = "Vox " + (typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "") + "  ·  everything runs locally";

        S.Dictation.StateChanged += (_, _) => Dispatcher.Invoke(RefreshStatus);
        S.ModelChanged += () => Dispatcher.Invoke(() => { RefreshStatus(); PopulateModels(); });
        S.Meeting.Status += m => Dispatcher.Invoke(() => MeetingStatus.Text = m);
        S.Meeting.LineAdded += (who, text) => Dispatcher.Invoke(() =>
        {
            LiveTranscript.Items.Add($"{who}: {text}");
            LiveTranscript.ScrollIntoView(LiveTranscript.Items[^1]);
        });
        _meetingTimer.Tick += (_, _) => UpdateMeetingStatus();
        _loading = false;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!S.Quitting) { e.Cancel = true; Hide(); return; }
        base.OnClosing(e);
    }

    // ---------- navigation ----------
    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PanelDictation == null) return;
        var tag = (sender as RadioButton)?.Tag as string;
        PanelDictation.Visibility = tag == "Dictation" ? Visibility.Visible : Visibility.Collapsed;
        PanelMeetings.Visibility = tag == "Meetings" ? Visibility.Visible : Visibility.Collapsed;
        PanelDictionary.Visibility = tag == "Dictionary" ? Visibility.Visible : Visibility.Collapsed;
        PanelSnippets.Visibility = tag == "Snippets" ? Visibility.Visible : Visibility.Collapsed;
        PanelSettings.Visibility = tag == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "Meetings") RefreshMeetingFiles();
    }

    public void ShowMeetingsTab() => NavMeetings.IsChecked = true;

    // ---------- status ----------
    public void RefreshStatus()
    {
        var st = S.Dictation.State;
        string model = S.Engine.IsLoaded ? S.Engine.ModelName : "";
        (string text, string color) = st switch
        {
            VoxState.Listening => ("Listening", "#FF5A4F"),
            VoxState.Processing => ("Transcribing", "#F5B942"),
            _ when S.ModelLoading => ("Loading model...", "#E0A030"),
            _ when !S.Engine.IsLoaded => ("No model", "#C0392B"),
            _ => ("Ready", "#3DBE8B"),
        };
        StatusText.Text = text;
        StatusDot.Fill = (Brush)new BrushConverter().ConvertFromString(color)!;
        StatusModel.Text = model;
        NoModelBanner.Visibility = !S.Engine.IsLoaded && !S.ModelLoading && !S.AnyModelAvailable ? Visibility.Visible : Visibility.Collapsed;
        HintText.Text = HotkeyHints();
    }

    string HotkeyHints()
    {
        var c = S.Settings.DictationCombo;
        string k = c.ToString(), paste = S.Settings.PasteLastCombo.ToString();
        string handsFree = c.Keys.Contains(Native.VK_SPACE) ? $"Double-tap {k}" : $"Add Space (or double-tap {k})";
        return $"Hold {k} and speak. Release to insert the text where your cursor is. {handsFree} for hands-free; press {k} again to finish. Esc cancels. {paste} pastes your last dictation.";
    }

    void UpdateHistoryCount() => HistoryCount.Text = S.Data.History.Count == 0 ? "Nothing yet. Hold your shortcut and say something." : $"{S.Data.History.Count} dictations";

    // ---------- history ----------
    void CopyEntry_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is HistoryEntry h) TextInjector.SetClipboard(h.Text);
    }
    void DeleteEntry_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is HistoryEntry h) S.Data.DeleteHistory(h);
    }
    void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Delete all dictation history? This cannot be undone.", "Vox", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            S.Data.ClearHistory();
    }
    void OpenData_Click(object sender, RoutedEventArgs e) => OpenPath(S.Data.Root);

    static void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception ex) { Log.Error("open " + path, ex); }
    }

    // ---------- grids ----------
    void Grid_Changed(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(new Action(() => { S.Data.SaveDictionary(); S.Data.SaveSnippets(); }), DispatcherPriority.Background);

    // ---------- meetings ----------
    async void Meeting_Click(object sender, RoutedEventArgs e)
    {
        Log.Info("meeting button clicked, running=" + S.Meeting.IsRunning);
        if (!S.Meeting.IsRunning)
        {
            try
            {
                LiveTranscript.Items.Clear();
                S.Meeting.Start(MeetingTitle.Text);
                MeetingButton.Content = "Stop and save";
                MeetingButton.Style = (Style)FindResource("DangerButton");
                MeetingTitle.IsEnabled = false;
                _meetingTimer.Start();
                S.NotifyMeeting(true);
            }
            catch (Exception ex)
            {
                Log.Error("meeting start", ex);
                MessageBox.Show(this, ex.Message, "Vox", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        else await StopMeetingAsync();
    }

    public async Task StopMeetingAsync()
    {
        if (!S.Meeting.IsRunning) return;
        MeetingButton.IsEnabled = false;
        _meetingTimer.Stop();
        MeetingStatus.Text = "Finishing the last bit of transcription...";
        var path = await S.Meeting.StopAsync();
        MeetingButton.Content = "Start recording";
        MeetingButton.Style = (Style)FindResource("PrimaryButton");
        MeetingButton.IsEnabled = true; MeetingTitle.IsEnabled = true;
        MeetingStatus.Text = path == null ? "Ready." : "Saved: " + Path.GetFileName(path);
        S.NotifyMeeting(false);
        RefreshMeetingFiles();
    }

    void UpdateMeetingStatus()
    {
        var m = S.Meeting;
        MeetingStatus.Text = $"Recording  {m.Elapsed:hh\\:mm\\:ss}   ·   {m.LineCount} lines transcribed" + (m.Pending > 0 ? $"   ·   {m.Pending} waiting" : "");
    }

    void RefreshMeetingFiles()
    {
        try
        {
            var dir = new DirectoryInfo(S.Data.MeetingsDir);
            MeetingFiles.ItemsSource = dir.Exists ? dir.GetFiles("*.md").OrderByDescending(f => f.LastWriteTime).ToList() : new List<FileInfo>();
        }
        catch { }
    }
    void OpenMeetings_Click(object sender, RoutedEventArgs e) => OpenPath(S.Data.MeetingsDir);
    void MeetingFile_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (MeetingFiles.SelectedItem is not FileInfo f) return;
        try { Process.Start(new ProcessStartInfo(f.FullName) { UseShellExecute = true }); }
        catch { try { Process.Start("notepad.exe", "\"" + f.FullName + "\""); } catch { } }
    }

    // ---------- shortcuts ----------
    void ShowKeys()
    {
        KeysDictation.Text = S.Settings.DictationCombo.ToString();
        KeysPaste.Text = S.Settings.PasteLastCombo.ToString();
        KeysCopy.Text = S.Settings.CopyLastCombo.ToString();
        HotkeyHelp.Text = $"Hands-free: press Space while holding {S.Settings.DictationCombo}, or double-tap it. Shortcuts work in every app, even when this window is closed.";
        HintText.Text = HotkeyHints();
    }

    Button? _capturingButton;

    void ChangeKeys_Click(object sender, RoutedEventArgs e)
    {
        var btn = (Button)sender;
        if (_capturingButton != null) { S.Hotkeys.CancelCapture(); return; }
        string which = (string)btn.Tag;
        var box = which switch { "paste" => KeysPaste, "copy" => KeysCopy, _ => KeysDictation };
        _capturingButton = btn;
        btn.Content = "Cancel";
        box.Text = "Press the keys now...";
        S.Hotkeys.BeginCapture(combo =>
        {
            _capturingButton = null;
            btn.Content = "Change...";
            if (combo != null)
            {
                var problem = combo.Problem();
                var others = new (string Name, KeyCombo Combo)[] { ("dictation", S.Settings.DictationCombo), ("paste", S.Settings.PasteLastCombo), ("copy", S.Settings.CopyLastCombo) }
                    .Where(o => o.Name != which && o.Combo.Equals(combo)).ToList();
                if (problem == null && others.Count > 0) problem = $"{combo} is already used for another Vox shortcut.";
                if (problem != null) MessageBox.Show(this, problem, "Shortcut not changed", MessageBoxButton.OK, MessageBoxImage.Information);
                else SaveKeys(which, combo.Serialize());
            }
            ShowKeys();
        });
    }

    void DefaultKeys_Click(object sender, RoutedEventArgs e)
    {
        if (_capturingButton != null) S.Hotkeys.CancelCapture();
        string which = (string)((Button)sender).Tag;
        var def = which switch { "paste" => KeyCombo.DefaultPasteLast, "copy" => KeyCombo.DefaultCopyLast, _ => KeyCombo.DefaultDictation };
        SaveKeys(which, def.Serialize());
        ShowKeys();
    }

    void SaveKeys(string which, string keys)
    {
        switch (which)
        {
            case "paste": S.Settings.PasteLastKeys = keys; break;
            case "copy": S.Settings.CopyLastKeys = keys; break;
            default: S.Settings.DictationKeys = keys; break;
        }
        S.Settings.Save();
        S.ApplySettings(false);
        Log.Info($"shortcut {which} set to {keys}");
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        // leaving the window while recording a shortcut: stop recording so the keyboard works normally elsewhere
        if (_capturingButton != null) S.Hotkeys.CancelCapture();
    }

    void RenameSpeakers_Click(object sender, RoutedEventArgs e)
    {
        if (MeetingFiles.SelectedItem is not FileInfo f)
        {
            MessageBox.Show(this, "Select a saved transcript in the list first.", "Rename speakers", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (S.Meeting.IsRunning && string.Equals(S.Meeting.FilePath, f.FullName, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "This meeting is still recording. Stop it first.", "Rename speakers", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            var names = MeetingRecorder.SpeakersIn(f.FullName);
            if (names.Count == 0) { MessageBox.Show(this, "No speaker labels found in this transcript.", "Rename speakers"); return; }
            var dlg = new RenameSpeakersWindow(names) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            MeetingRecorder.RenameSpeakers(f.FullName, dlg.Renames);
            RefreshMeetingFiles();
            MeetingStatus.Text = "Renamed speakers in " + f.Name;
        }
        catch (Exception ex) { MessageBox.Show(this, "Could not rename speakers.\n\n" + ex.Message, "Vox", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    void UpdateSpeakerModelStatus()
    {
        var (seg, emb) = SpeakerDiarizer.FindModels(S.Data.ModelsDir);
        SpeakerModelStatus.Text = seg != null && emb != null
            ? $"Installed: {Path.GetFileName(seg)} and {Path.GetFileName(emb)}."
            : seg != null
                ? "One more file needed: download nemo_en_titanet_small.onnx (38 MB) from github.com/k2-fsa/sherpa-onnx/releases (the \"speaker-recongition-models\" release), then click Install speaker models and pick it. Vox never downloads anything itself."
                : "Not installed. Speaker labels need two model files from github.com/k2-fsa/sherpa-onnx/releases: the pyannote segmentation model (model.onnx) and nemo_en_titanet_small.onnx. Vox never downloads anything itself.";
    }

    void ImportSpeakerModels_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Choose the speaker model files (.onnx)", Filter = "Speaker models (*.onnx)|*.onnx", Multiselect = true };
        if (dlg.ShowDialog(this) != true) return;
        try { MessageBox.Show(this, SpeakerDiarizer.Import(S.Data.ModelsDir, dlg.FileNames), "Speaker models"); }
        catch (Exception ex) { MessageBox.Show(this, "Could not install the speaker models.\n\n" + ex.Message, "Vox", MessageBoxButton.OK, MessageBoxImage.Warning); }
        UpdateSpeakerModelStatus();
    }

    // ---------- settings ----------
    void PopulateSettings()
    {
        FolderBox.Text = S.Data.Root;
        PopulateModels();

        MicCombo.Items.Clear();
        MicCombo.Items.Add(new ComboBoxItem { Content = "Windows default", Tag = "" });
        foreach (var d in MicRecorder.Devices()) MicCombo.Items.Add(new ComboBoxItem { Content = d, Tag = d });
        SelectByTag(MicCombo, S.Settings.MicrophoneName);

        LangCombo.Items.Clear();
        foreach (var (c, n) in Languages) LangCombo.Items.Add(new ComboBoxItem { Content = n, Tag = c });
        SelectByTag(LangCombo, S.Settings.Language);

        ShowKeys();

        InsertCombo.Items.Clear();
        InsertCombo.Items.Add(new ComboBoxItem { Content = "Pasting (fast, works in almost every app)", Tag = InsertMethod.Paste.ToString() });
        InsertCombo.Items.Add(new ComboBoxItem { Content = "Typing each character (slower; use if an app blocks paste)", Tag = InsertMethod.Type.ToString() });
        SelectByTag(InsertCombo, S.Settings.Insert.ToString());

        ThreadsCombo.Items.Clear();
        ThreadsCombo.Items.Add(new ComboBoxItem { Content = "Automatic", Tag = "0" });
        for (int i = 1; i <= Environment.ProcessorCount; i++) ThreadsCombo.Items.Add(new ComboBoxItem { Content = i + (i == 1 ? " thread" : " threads"), Tag = i.ToString() });
        SelectByTag(ThreadsCombo, S.Settings.Threads.ToString());
        CpuHelp.Text = $"This PC has {Environment.ProcessorCount} logical processors. More threads = faster transcription, up to the number of physical cores.";

        ChkLive.IsChecked = S.Settings.LiveTranscribe;
        ChkHints.IsChecked = S.Settings.AccurateHints;
        ChkTrailing.IsChecked = S.Settings.AppendTrailingSpace;
        ChkFillers.IsChecked = S.Settings.RemoveFillers;
        ChkNumbers.IsChecked = S.Settings.FormatNumbers;
        ChkCommands.IsChecked = S.Settings.VoiceCommands;
        ChkVoxBar.IsChecked = S.Settings.ShowVoxBarAlways;
        ChkLaunch.IsChecked = S.Settings.LaunchAtLogin;
        ChkHistory.IsChecked = S.Settings.SaveHistory;
        ChkAudio.IsChecked = S.Settings.KeepAudio;
        ChkSysAudio.IsChecked = S.Settings.MeetingIncludeSystemAudio;

        MeetPlaybackCombo.Items.Clear();
        MeetPlaybackCombo.Items.Add(new ComboBoxItem { Content = "Every playback device (recommended)", Tag = "" });
        MeetPlaybackCombo.Items.Add(new ComboBoxItem { Content = "Windows default devices only", Tag = "default" });
        foreach (var (id, name) in MeetingRecorder.PlaybackDevices()) MeetPlaybackCombo.Items.Add(new ComboBoxItem { Content = "Only: " + name, Tag = id });
        SelectByTag(MeetPlaybackCombo, S.Settings.MeetingPlayback);
        if (MeetPlaybackCombo.SelectedIndex < 0) MeetPlaybackCombo.SelectedIndex = 0;
        ChkSpeakers.IsChecked = S.Settings.MeetingSpeakerLabels;
        UpdateSpeakerModelStatus();
    }

    void PopulateModels()
    {
        bool was = _loading; _loading = true;
        ModelCombo.Items.Clear();
        foreach (var f in S.Data.FindModels(AppContext.BaseDirectory))
            ModelCombo.Items.Add(new ComboBoxItem { Content = $"{ModelLabel(f.Name)}   ({f.Length / 1048576} MB)", Tag = f.Name });
        SelectByTag(ModelCombo, S.Settings.ModelFile);
        if (ModelCombo.SelectedIndex < 0 && ModelCombo.Items.Count > 0 && S.Engine.IsLoaded)
            SelectByTag(ModelCombo, S.Engine.ModelName + ".bin");
        _loading = was;
    }

    static string ModelLabel(string file)
    {
        var n = Path.GetFileNameWithoutExtension(file);
        if (QwenEngine.IsGgufModel(file))
            return (n.Contains("qwen3", StringComparison.OrdinalIgnoreCase) ? "Accurate mode: Qwen3-ASR" : "Accurate mode: " + n) + ", slower";
        if (n.Contains("parakeet-tdt-0.6b-v3", StringComparison.OrdinalIgnoreCase)) return "Fast: " + n;
        return n;
    }

    static void SelectByTag(ComboBox box, string tag)
    {
        foreach (ComboBoxItem it in box.Items)
            if (string.Equals(it.Tag as string, tag, StringComparison.OrdinalIgnoreCase)) { box.SelectedItem = it; return; }
        if (box.Items.Count > 0 && box.SelectedIndex < 0 && box != null && tag == "") box.SelectedIndex = 0;
    }

    static string TagOf(ComboBox b, string fallback = "") => (b.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;

    void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsLoaded) return;
        var s = S.Settings;
        var newModel = TagOf(ModelCombo, s.ModelFile);
        bool modelChanged = !string.Equals(newModel, s.ModelFile, StringComparison.OrdinalIgnoreCase) && newModel != "";
        s.ModelFile = newModel;
        s.MicrophoneName = TagOf(MicCombo);
        s.Language = TagOf(LangCombo, "auto");
        if (Enum.TryParse<InsertMethod>(TagOf(InsertCombo), out var im)) s.Insert = im;
        s.Threads = int.TryParse(TagOf(ThreadsCombo, "0"), out var th) ? th : 0;
        s.LiveTranscribe = ChkLive.IsChecked == true;
        s.AccurateHints = ChkHints.IsChecked == true;
        s.AppendTrailingSpace = ChkTrailing.IsChecked == true;
        s.RemoveFillers = ChkFillers.IsChecked == true;
        s.FormatNumbers = ChkNumbers.IsChecked == true;
        s.VoiceCommands = ChkCommands.IsChecked == true;
        s.ShowVoxBarAlways = ChkVoxBar.IsChecked == true;
        s.LaunchAtLogin = ChkLaunch.IsChecked == true;
        s.SaveHistory = ChkHistory.IsChecked == true;
        s.KeepAudio = ChkAudio.IsChecked == true;
        s.MeetingIncludeSystemAudio = ChkSysAudio.IsChecked == true;
        s.MeetingPlayback = TagOf(MeetPlaybackCombo, s.MeetingPlayback);
        s.MeetingSpeakerLabels = ChkSpeakers.IsChecked == true;
        s.Save();
        S.ApplySettings(modelChanged);
        HintText.Text = HotkeyHints();
    }

    void ImportModel_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Choose a speech model", Filter = "Speech models (*.bin, *.gguf)|*.bin;*.gguf|All files|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            var name = Path.GetFileName(dlg.FileName);
            if (!QwenEngine.IsGgufModel(name) && !name.StartsWith("ggml-", StringComparison.OrdinalIgnoreCase)) name = "ggml-" + name;
            var dest = Path.Combine(S.Data.ModelsDir, name);
            if (!string.Equals(Path.GetFullPath(dlg.FileName), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                File.Copy(dlg.FileName, dest, true);
            S.Settings.ModelFile = name; S.Settings.Save();
            PopulateModels();
            S.ApplySettings(true);
        }
        catch (Exception ex) { MessageBox.Show(this, "Could not import the model.\n\n" + ex.Message, "Vox", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { Mouse.OverrideCursor = null; }
    }

    void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new WinForms.FolderBrowserDialog { Description = "Choose where Vox saves its data", SelectedPath = S.Data.Root, UseDescriptionForTitle = true };
        if (dlg.ShowDialog() != WinForms.DialogResult.OK) return;
        if (MessageBox.Show(this, $"Vox will use:\n{dlg.SelectedPath}\n\nYour existing files are not moved (copy them there if you want them). Restart Vox now?", "Vox", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        S.Settings.DataFolder = dlg.SelectedPath; S.Settings.Save();
        S.Restart();
    }

    void OpenLog_Click(object sender, RoutedEventArgs e) => OpenPath(Log.Path_);

    static class Mouse { public static System.Windows.Input.Cursor? OverrideCursor { set => System.Windows.Input.Mouse.OverrideCursor = value; } }
}
