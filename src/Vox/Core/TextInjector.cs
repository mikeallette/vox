using System.Windows;
using static Vox.Core.Native;

namespace Vox.Core;

/// <summary>
/// Inserts text at the caret of whatever app has focus. Paste (Ctrl+V with the clipboard saved and
/// restored) works in nearly everything, including Electron apps like VS Code/Cursor where
/// accessibility-based insertion silently fails. "Type" sends Unicode key events instead.
/// Must run on the UI (STA) thread for clipboard access.
/// </summary>
public enum InsertResult { Inserted, ClipboardOnly, Failed }

public sealed class TextInjector
{
    public async Task<InsertResult> InsertAsync(string text, InsertMethod method)
    {
        if (string.IsNullOrEmpty(text)) return InsertResult.Inserted;
        // Windows silently drops Vox's keystrokes into admin windows; leave the text on the clipboard instead of
        // pretending it was typed
        if (ForegroundIsElevatedAboveUs())
        {
            Log.Info("insert: focused window runs as administrator; text left on the clipboard");
            return SetClipboard(text) ? InsertResult.ClipboardOnly : InsertResult.Failed;
        }
        await WaitForModifiersReleased();
        if (method == InsertMethod.Type) { TypeText(text); return InsertResult.Inserted; }

        string? saved = null; bool hadText = false;
        try { if (Clipboard.ContainsText()) { saved = Clipboard.GetText(); hadText = true; } } catch { }

        if (!SetClipboard(text)) { TypeText(text); return InsertResult.Inserted; }
        await Task.Delay(40);
        Send(Key((ushort)VK_CONTROL, false), Key((ushort)'V', false), Key((ushort)'V', true), Key((ushort)VK_CONTROL, true));
        await Task.Delay(220);                       // let the target app read the clipboard before restoring
        try { if (hadText && saved != null) SetClipboard(saved); } catch { }
        return InsertResult.Inserted;
    }

    public static bool SetClipboard(string text)
    {
        for (int i = 0; i < 6; i++)
        {
            try { Clipboard.SetDataObject(text, true); return true; }
            catch { Thread.Sleep(40); }
        }
        return false;
    }

    static async Task WaitForModifiersReleased()
    {
        // A held Win/Alt/Shift would turn Ctrl+V into a different shortcut (Win+Ctrl+V, Alt+Ctrl+V...). Wait up to
        // 1.5 s for the keys to come up; if one is still held (hands-free, or a key held for something else), tell
        // Windows it is up before pasting. SendInput doesn't do that by itself.
        int[] mods = { VK_LWIN, VK_RWIN, VK_LMENU, VK_RMENU, VK_LSHIFT, VK_RSHIFT, VK_LCONTROL, VK_RCONTROL };
        for (int i = 0; i < 38; i++)
        {
            if (!mods.Any(IsDown)) return;
            await Task.Delay(40);
        }
        var held = mods.Where(IsDown).ToArray();
        Log.Info($"insert: keys still held ({string.Join(",", held.Select(k => $"0x{k:X2}"))}); releasing them before the paste");
        var ups = new List<INPUT> { Key((ushort)VK_MASK, false), Key((ushort)VK_MASK, true) };   // stops a Win release opening Start
        ups.AddRange(held.Select(k => Key((ushort)k, true)));
        Send(ups.ToArray());
        await Task.Delay(30);
    }

    static void TypeText(string text)
    {
        var inputs = new List<INPUT>();
        foreach (var ch in text.Replace("\r\n", "\n"))
        {
            if (ch == '\n') { inputs.Add(Key(0x0D, false)); inputs.Add(Key(0x0D, true)); }
            else { inputs.Add(UnicodeChar(ch, false)); inputs.Add(UnicodeChar(ch, true)); }
            if (inputs.Count >= 200) { Send(inputs.ToArray()); inputs.Clear(); }
        }
        if (inputs.Count > 0) Send(inputs.ToArray());
    }
}
