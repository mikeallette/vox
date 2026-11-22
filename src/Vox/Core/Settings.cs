using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vox.Core;

public enum HotkeyPreset { CtrlWin, CtrlAlt, F9 }
public enum InsertMethod { Paste, Type }

/// <summary>
/// Preferences. Stored in %APPDATA%\Vox\settings.json (no dictation content, only preferences
/// and the path of the user's data folder). All dictation content lives in the data folder.
/// </summary>
public sealed class AppSettings
{
    public string DataFolder { get; set; } = "";
    public string ModelFile { get; set; } = "";          // file name of a ggml-*.bin model
    public string Language { get; set; } = "auto";       // "auto", "en", ... (forced "en" for *.en models)
    public int Threads { get; set; } = 0;                // 0 = auto
    public string MicrophoneName { get; set; } = "";     // "" = Windows default
    public HotkeyPreset Hotkey { get; set; } = HotkeyPreset.CtrlWin;   // older settings files; DictationKeys wins when set
    public string DictationKeys { get; set; } = "";      // hold to dictate, e.g. "Ctrl+Win" (any combo, see KeyCombo)
    public string PasteLastKeys { get; set; } = "";      // paste the last dictation again ("" = Alt+Shift+Z)
    public string CopyLastKeys { get; set; } = "";       // copy the last dictation ("" = Alt+Shift+X)

    [JsonIgnore] public KeyCombo DictationCombo =>
        KeyCombo.Parse(DictationKeys) ?? Hotkey switch
        {
            HotkeyPreset.CtrlAlt => KeyCombo.Parse("Ctrl+Alt")!,
            HotkeyPreset.F9 => KeyCombo.Parse("F9")!,
            _ => KeyCombo.DefaultDictation,
        };
    [JsonIgnore] public KeyCombo PasteLastCombo => KeyCombo.Parse(PasteLastKeys) ?? KeyCombo.DefaultPasteLast;
    [JsonIgnore] public KeyCombo CopyLastCombo => KeyCombo.Parse(CopyLastKeys) ?? KeyCombo.DefaultCopyLast;
    public InsertMethod Insert { get; set; } = InsertMethod.Paste;
    public bool LiveTranscribe { get; set; } = true;     // transcribe finished phrases while still speaking
    // accurate mode: pass dictionary words to Qwen3 as hints. Off by default: on 25 read sentences they fixed 3 words,
    // broke 4 and added ~1.4 s per dictation (2026-11-21)
    public bool AccurateHints { get; set; } = false;
    public bool AppendTrailingSpace { get; set; } = true;
    public bool RemoveFillers { get; set; } = true;
    public bool FormatNumbers { get; set; } = true;      // "forty eight volts" -> "48 V", "8 10 pm" -> "8:10 p.m."
    public bool VoiceCommands { get; set; } = true;
    public bool ShowVoxBarAlways { get; set; } = true;
    public bool LaunchAtLogin { get; set; } = false;
    public bool SaveHistory { get; set; } = true;
    public bool KeepAudio { get; set; } = false;         // save each dictation's WAV next to history
    public List<float> RecentSpeechLevels { get; set; } = new();   // last 20 dictations' speech level, for the "mic is quiet" warning
    public bool MeetingIncludeSystemAudio { get; set; } = true;
    public string MeetingPlayback { get; set; } = "";    // "" = every playback device, "default" = Windows defaults, else a device ID
    public bool MeetingSpeakerLabels { get; set; } = true; // label other voices Speaker 1, 2, ... (needs the speaker models)

    /// <summary>VOX_SETTINGS_DIR (test tools only) runs Vox against a throwaway settings folder instead of the real one.</summary>
    [JsonIgnore] public static string SettingsDir =>
        Environment.GetEnvironmentVariable("VOX_SETTINGS_DIR") is { Length: > 0 } test ? test
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vox");
    [JsonIgnore] public static string SettingsPath => Path.Combine(SettingsDir, "settings.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    /// <summary>Set when settings.json existed but could not be read (shown once at startup).</summary>
    [JsonIgnore] public static string? LoadProblem { get; private set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Json) ?? new();
        }
        catch (Exception ex)
        {
            Log.Error("settings load", ex);
            LoadProblem = "Your settings file could not be read, so Vox started with default settings. The original was kept as:\n" + DataStore.KeepBadCopy(SettingsPath);
        }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            AtomicFile.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) { Log.Error("settings save", ex); }
    }
}

internal static class AtomicFile
{
    public static void WriteAllText(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }
}

internal static class Log
{
    static readonly object Gate = new();
    public static string Path_ => System.IO.Path.Combine(AppSettings.SettingsDir, "vox.log");

    public static void Info(string msg) => Write("INFO", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERROR", ex == null ? msg : $"{msg}: {ex}");

    static void Write(string level, string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppSettings.SettingsDir);
                var fi = new FileInfo(Path_);
                if (fi.Exists && fi.Length > 1_000_000) File.Move(Path_, Path_ + ".old", true);
                File.AppendAllText(Path_, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {msg}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
