using System.Collections.ObjectModel;
using System.Text.Json;

namespace Vox.Core;

public sealed class HistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Time { get; set; } = DateTime.Now;
    public string Text { get; set; } = "";
    public string RawText { get; set; } = "";
    public double AudioSeconds { get; set; }
    public int TranscribeMs { get; set; }
    public string App { get; set; } = "";
    public string Model { get; set; } = "";
    public int Words => string.IsNullOrWhiteSpace(Text) ? 0 : Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    public string TimeLabel => Time.ToString("ddd MMM d, h:mm tt");
}

/// <summary>"Heard as" (may be empty) -> "Write as". Empty From = a term that only biases recognition and fixes casing.</summary>
public sealed class DictEntry
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

public sealed class Snippet
{
    public string Trigger { get; set; } = "";
    public string Text { get; set; } = "";
}

/// <summary>
/// Everything the user creates lives under one folder they choose:
///   history.jsonl, dictionary.json, snippets.json, Meetings\*.md, Audio\*.wav (optional), models\*.bin
/// Plain files only, so it is easy to back up, inspect, or delete.
/// </summary>
public sealed class DataStore
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    readonly object _gate = new();

    public string Root { get; }
    public string MeetingsDir => Path.Combine(Root, "Meetings");
    public string AudioDir => Path.Combine(Root, "Audio");
    public string ModelsDir => Path.Combine(Root, "models");
    string HistoryPath => Path.Combine(Root, "history.jsonl");
    string DictionaryPath => Path.Combine(Root, "dictionary.json");
    string SnippetsPath => Path.Combine(Root, "snippets.json");

    public ObservableCollection<HistoryEntry> History { get; } = new();
    public ObservableCollection<DictEntry> Dictionary { get; } = new();
    public ObservableCollection<Snippet> Snippets { get; } = new();

    public DataStore(string root)
    {
        Root = root;
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(MeetingsDir);
        Directory.CreateDirectory(ModelsDir);
        LoadAll();
        WatchForOutsideEdits();
    }

    // ---- dictionary.json / snippets.json edited outside Vox while it runs: reload instead of overwriting it later ----
    readonly Dictionary<string, DateTime> _stamp = new(StringComparer.OrdinalIgnoreCase);
    FileSystemWatcher? _watcher;
    System.Threading.Timer? _reloadTimer;

    void Stamp(string path) { try { _stamp[path] = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default; } catch { } }

    void WatchForOutsideEdits()
    {
        Stamp(DictionaryPath); Stamp(SnippetsPath);
        var ui = SynchronizationContext.Current;
        if (ui == null) return;                         // command-line tools: nothing to keep in sync
        try
        {
            _watcher = new FileSystemWatcher(Root) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName, IncludeSubdirectories = false, EnableRaisingEvents = true };
            // editors save in bursts (temp file, rename, write): wait until it settles, then reload on the UI thread
            _reloadTimer = new System.Threading.Timer(_ => ui.Post(_ => ReloadIfChanged(), null));
            FileSystemEventHandler onChange = (_, e) =>
            {
                if (e.Name is not null && (e.Name.Equals("dictionary.json", StringComparison.OrdinalIgnoreCase) || e.Name.Equals("snippets.json", StringComparison.OrdinalIgnoreCase)))
                    _reloadTimer.Change(700, Timeout.Infinite);
            };
            _watcher.Changed += onChange; _watcher.Created += onChange;
            _watcher.Renamed += (s, e) => onChange(s, e);
        }
        catch (Exception ex) { Log.Error("watch data folder", ex); }
    }

    void ReloadIfChanged()
    {
        lock (_gate)
        {
            Reload(DictionaryPath, "dictionary", Dictionary);
            Reload(SnippetsPath, "snippets", Snippets);
        }
    }

    void Reload<T>(string path, string what, ObservableCollection<T> target)
    {
        try
        {
            if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) == _stamp.GetValueOrDefault(path)) return;
            var items = JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path)) ?? new();
            target.Clear();
            foreach (var i in items) target.Add(i);
            Stamp(path);
            Log.Info($"{what}: changed outside Vox, reloaded {items.Count} entries");
        }
        catch (Exception ex) { Log.Error($"{what}: outside edit could not be read, keeping the current list", ex); }
    }

    /// <summary>Files that could not be read at startup. Each was copied aside first, so a later save can't destroy it.</summary>
    public List<string> LoadProblems { get; } = new();

    void LoadAll()
    {
        try
        {
            if (File.Exists(HistoryPath))
            {
                var items = new List<HistoryEntry>();
                foreach (var line in File.ReadLines(HistoryPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try { var e = JsonSerializer.Deserialize<HistoryEntry>(line); if (e != null) items.Add(e); } catch { }
                }
                foreach (var e in items.OrderByDescending(x => x.Time).Take(500)) History.Add(e);
            }
        }
        catch (Exception ex) { Log.Error("history load", ex); }
        // each file on its own, so a damaged dictionary doesn't also lose the snippets (QA T9, 2026-11-09)
        foreach (var d in LoadList<DictEntry>(DictionaryPath, "dictionary")) Dictionary.Add(d);
        foreach (var s in LoadList<Snippet>(SnippetsPath, "snippets")) Snippets.Add(s);
    }

    List<T> LoadList<T>(string path, string what)
    {
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path)) ?? new(); }
        catch (Exception ex)
        {
            Log.Error(what + " load", ex);
            var keep = KeepBadCopy(path);
            LoadProblems.Add($"Your {what} file could not be read, so Vox started without it. The original was kept as:\n{keep}");
            return new();
        }
    }

    /// <summary>Copies a file that failed to load to name.bad-yyyyMMdd-HHmmss.ext, so nothing the user made is overwritten.</summary>
    internal static string KeepBadCopy(string path, string tag = "bad")
    {
        try
        {
            var keep = Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}.{tag}-{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension(path)}");
            File.Copy(path, keep, overwrite: false);
            return keep;
        }
        catch (Exception ex) { Log.Error("keep bad copy of " + path, ex); return path; }
    }

    public void AddHistory(HistoryEntry e)
    {
        lock (_gate)
        {
            try { File.AppendAllText(HistoryPath, JsonSerializer.Serialize(e) + Environment.NewLine); }
            catch (Exception ex) { Log.Error("history append", ex); }
        }
        History.Insert(0, e);
    }

    /// <summary>
    /// Removes one entry from the file by its Id. The window only shows the newest 500, so the file is filtered line by
    /// line instead of rewritten from that list (which would erase older entries). QA T9, 2026-11-09.
    /// </summary>
    public void DeleteHistory(HistoryEntry e)
    {
        History.Remove(e);
        lock (_gate)
        {
            try
            {
                if (!File.Exists(HistoryPath)) return;
                var keep = File.ReadAllLines(HistoryPath).Where(line =>
                {
                    if (string.IsNullOrWhiteSpace(line)) return false;
                    try { return JsonSerializer.Deserialize<HistoryEntry>(line)?.Id != e.Id; } catch { return true; }   // unreadable lines are kept as they are
                });
                AtomicFile.WriteAllText(HistoryPath, string.Concat(keep.Select(l => l + Environment.NewLine)));
            }
            catch (Exception ex) { Log.Error("history delete", ex); }
        }
    }

    public void ClearHistory()
    {
        History.Clear();
        lock (_gate) { try { File.WriteAllText(HistoryPath, ""); } catch { } }
    }

    public void SaveDictionary() => SaveList(DictionaryPath, "dictionary", Dictionary.Where(d => !string.IsNullOrWhiteSpace(d.To)).ToList());

    public void SaveSnippets() => SaveList(SnippetsPath, "snippets", Snippets.Where(s => !string.IsNullOrWhiteSpace(s.Trigger)).ToList());

    void SaveList<T>(string path, string what, List<T> items)
    {
        lock (_gate)
        {
            try
            {
                // changed outside Vox since we last read or wrote it: keep that version instead of silently replacing it
                if (File.Exists(path) && _stamp.TryGetValue(path, out var known) && File.GetLastWriteTimeUtc(path) != known)
                    Log.Info($"{what}: changed outside Vox before this save; kept that version as {KeepBadCopy(path, "outside-edit")}");
                AtomicFile.WriteAllText(path, JsonSerializer.Serialize(items, Json));
                Stamp(path);
            }
            catch (Exception ex) { Log.Error(what + " save", ex); }
        }
    }

    /// <summary>Whisper-style ggml models found next to the app or in the data folder.</summary>
    public IEnumerable<FileInfo> FindModels(string appDir)
    {
        var dirs = new[] { Path.Combine(appDir, "models"), ModelsDir }.Where(Directory.Exists);
        return dirs.SelectMany(d => new DirectoryInfo(d).GetFiles("ggml-*.bin")
                                     .Concat(QwenEngine.IsAvailable ? new DirectoryInfo(d).GetFiles("*.gguf") : Array.Empty<FileInfo>()))
                   .Where(f => !f.Name.StartsWith("ggml-silero", StringComparison.OrdinalIgnoreCase))   // the speech detector, not a speech model
                   .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                   .OrderBy(f => f.Length);
    }
}
