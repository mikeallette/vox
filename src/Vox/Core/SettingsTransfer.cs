using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vox.Core;

/// <summary>
/// One file that carries the dictionary, snippets and the portable settings to another PC (home to work).
/// Machine-specific choices (data folder, microphone, model file, threads, meeting playback device, mic levels) and
/// history stay behind. Importing adds dictionary entries and snippets that aren't there yet and never deletes; the
/// current dictionary, snippets and settings are backed up first.
/// </summary>
internal static class SettingsTransfer
{
    static readonly string[] Portable =
    {
        nameof(AppSettings.Language), nameof(AppSettings.DictationKeys), nameof(AppSettings.PasteLastKeys), nameof(AppSettings.CopyLastKeys),
        nameof(AppSettings.RehearKeys), nameof(AppSettings.Insert), nameof(AppSettings.LiveTranscribe), nameof(AppSettings.AccurateHints),
        nameof(AppSettings.AccurateApps), nameof(AppSettings.AppendTrailingSpace), nameof(AppSettings.RemoveFillers), nameof(AppSettings.FormatNumbers),
        nameof(AppSettings.VoiceCommands), nameof(AppSettings.ShowVoxBarAlways), nameof(AppSettings.SaveHistory), nameof(AppSettings.KeepAudio),
        nameof(AppSettings.MeetingIncludeSystemAudio), nameof(AppSettings.MeetingSpeakerLabels),
    };

    static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },   // same as settings.json
    };

    public static void Export(string path, AppSettings s, DataStore d)
    {
        var all = JsonSerializer.SerializeToNode(s, Opts)!.AsObject();
        var settings = new JsonObject();
        foreach (var k in Portable) if (all.TryGetPropertyValue(k, out var v)) settings[k] = v?.DeepClone();
        var root = new JsonObject
        {
            ["vox"] = "settings-export",
            ["version"] = 1,
            ["exported"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            ["settings"] = settings,
            ["dictionary"] = JsonSerializer.SerializeToNode(d.Dictionary.ToList(), Opts),
            ["snippets"] = JsonSerializer.SerializeToNode(d.Snippets.ToList(), Opts),
        };
        File.WriteAllText(path, root.ToJsonString(Opts), new UTF8Encoding(false));
    }

    /// <summary>Returns a one-line summary of what changed.</summary>
    /// <param name="saveSettings">false only in tests: leaves settings.json alone (no backup, no save)</param>
    public static string Import(string path, AppSettings s, DataStore d, bool saveSettings = true)
    {
        var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new InvalidDataException("empty file");
        if ((string?)root["vox"] != "settings-export") throw new InvalidDataException("This isn't a Vox settings export file.");

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var toBackUp = new List<string> { Path.Combine(d.Root, "dictionary.json"), Path.Combine(d.Root, "snippets.json") };
        if (saveSettings) toBackUp.Add(AppSettings.SettingsPath);
        foreach (var f in toBackUp)
            if (File.Exists(f)) File.Copy(f, Path.Combine(Path.GetDirectoryName(f)!, Path.GetFileNameWithoutExtension(f) + $".before-import-{stamp}.json"), true);

        int addedDict = 0, sameDict = 0, clashDict = 0;
        foreach (var e in root["dictionary"]?.Deserialize<List<DictEntry>>() ?? new())
        {
            if (string.IsNullOrWhiteSpace(e.From) || string.IsNullOrWhiteSpace(e.To)) continue;
            var have = d.Dictionary.FirstOrDefault(x => x.From.Equals(e.From, StringComparison.OrdinalIgnoreCase));
            if (have == null) { d.Dictionary.Add(e); addedDict++; }
            else if (have.To == e.To) sameDict++;
            else clashDict++;                                        // keep this PC's version
        }
        int addedSn = 0;
        foreach (var sn in root["snippets"]?.Deserialize<List<Snippet>>() ?? new())
        {
            if (string.IsNullOrWhiteSpace(sn.Trigger) || d.Snippets.Any(x => x.Trigger.Equals(sn.Trigger, StringComparison.OrdinalIgnoreCase))) continue;
            d.Snippets.Add(sn); addedSn++;
        }
        if (addedDict > 0) d.SaveDictionary();
        if (addedSn > 0) d.SaveSnippets();

        int setCount = 0;
        if (root["settings"] is JsonObject inSet)
        {
            var mine = JsonSerializer.SerializeToNode(s, Opts)!.AsObject();
            foreach (var k in Portable)
                if (inSet.TryGetPropertyValue(k, out var v)) { mine[k] = v?.DeepClone(); setCount++; }
            var merged = mine.Deserialize<AppSettings>(Opts) ?? s;
            foreach (var p in typeof(AppSettings).GetProperties().Where(p => Portable.Contains(p.Name) && p.CanWrite))
                p.SetValue(s, p.GetValue(merged));
            if (saveSettings) s.Save();
        }
        return $"Added {addedDict} dictionary entries ({sameDict} already here{(clashDict > 0 ? $", {clashDict} kept as they were on this PC" : "")}), "
             + $"{addedSn} snippets, and {setCount} settings. The old files were backed up as *.before-import-{stamp}.json.";
    }
}
