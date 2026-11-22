using System.Text;
using static Vox.Core.Native;

namespace Vox.Core;

/// <summary>
/// Vox.exe --hotkey-test result.txt : drives HotkeyService with simulated key presses (no real keyboard, no hook)
/// and checks what fires and what is swallowed for several shortcut choices, plus shortcut recording and validation.
/// </summary>
internal static class HotkeyTest
{
    sealed class Inline : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
        public override void Send(SendOrPostCallback d, object? state) => d(state);
    }

    public static void Run(string[] args)
    {
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "") { if (ok) pass++; else fail++; sb.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "   [" + detail + "]" : "")}"); }

        try
        {
            var held = new bool[256];
            var events = new List<string>();
            HotkeyService New(string dict, string? paste = null, string? copy = null)
            {
                Array.Clear(held); events.Clear();
                var h = new HotkeyService(new Inline())
                {
                    Dictation = KeyCombo.Parse(dict)!, PasteLast = KeyCombo.Parse(paste ?? "Alt+Shift+Z")!, CopyLast = KeyCombo.Parse(copy ?? "Alt+Shift+X")!,
                    OsKeyDown = v => held[v], TestMode = true,
                };
                h.DictationStarted += m => events.Add("start:" + m);
                h.DictationFinished += () => events.Add("finish");
                h.DictationCancelled += s => events.Add(s ? "tap" : "cancel");
                h.PasteLastRequested += () => events.Add("paste");
                h.CopyLastRequested += () => events.Add("copy");
                return h;
            }
            var swallowed = new List<string>();
            bool Dn(HotkeyService h, int vk) { held[vk] = true; var s = h.Feed(vk, true); if (s) swallowed.Add("v" + KeyCombo.NameOf(vk)); return s; }
            bool Up(HotkeyService h, int vk) { held[vk] = false; var s = h.Feed(vk, false); if (s) swallowed.Add("^" + KeyCombo.NameOf(vk)); return s; }
            void Hold(int ms) => Thread.Sleep(ms);
            string Ev() => string.Join(",", events);

            // 1. Ctrl+Win push-to-talk
            var h = New("Ctrl+Win"); swallowed.Clear();
            Dn(h, VK_LCONTROL); Dn(h, VK_LWIN); Hold(350); Up(h, VK_LWIN); Up(h, VK_LCONTROL);
            Check("Ctrl+Win hold = push-to-talk", Ev() == "start:PushToTalk,finish", Ev());
            Check("Ctrl+Win keys are not swallowed (Start menu is masked instead)", swallowed.Count == 0, string.Join(" ", swallowed));
            // right-side keys count too
            h = New("Ctrl+Win"); Dn(h, VK_RCONTROL); Dn(h, VK_RWIN); Hold(350); Up(h, VK_RWIN); Up(h, VK_RCONTROL);
            Check("Right Ctrl + right Win works too", Ev() == "start:PushToTalk,finish", Ev());
            // extra modifier: not the shortcut
            h = New("Ctrl+Win"); Dn(h, VK_LCONTROL); Dn(h, VK_LMENU); Dn(h, VK_LWIN); Hold(50); Up(h, VK_LWIN); Up(h, VK_LMENU); Up(h, VK_LCONTROL);
            Check("Ctrl+Alt+Win does not trigger Ctrl+Win", events.Count == 0, Ev());
            // Ctrl+Win + Space = hands-free, again = finish
            h = New("Ctrl+Win"); swallowed.Clear();
            Dn(h, VK_LCONTROL); Dn(h, VK_LWIN); Dn(h, VK_SPACE); Up(h, VK_SPACE); Up(h, VK_LWIN); Up(h, VK_LCONTROL);
            Dn(h, VK_LCONTROL); Dn(h, VK_LWIN); Up(h, VK_LWIN); Up(h, VK_LCONTROL);
            Check("Ctrl+Win+Space = hands-free, Ctrl+Win again = finish", Ev() == "start:PushToTalk,start:HandsFree,finish", Ev());
            Check("Space in Ctrl+Win+Space is swallowed", swallowed.SequenceEqual(new[] { "vSpace", "^Space" }), string.Join(" ", swallowed));
            // double tap = hands-free
            h = New("Ctrl+Win");
            Dn(h, VK_LCONTROL); Dn(h, VK_LWIN); Up(h, VK_LWIN); Up(h, VK_LCONTROL);
            Dn(h, VK_LCONTROL); Dn(h, VK_LWIN); Up(h, VK_LWIN); Up(h, VK_LCONTROL);
            Check("Double-tap Ctrl+Win = hands-free", Ev() == "start:PushToTalk,tap,start:HandsFree", Ev());
            // Esc cancels
            h = New("Ctrl+Win"); swallowed.Clear();
            Dn(h, VK_LCONTROL); Dn(h, VK_LWIN); Hold(50); Dn(h, VK_ESCAPE); Up(h, VK_ESCAPE); Up(h, VK_LWIN); Up(h, VK_LCONTROL);
            Check("Esc cancels and is swallowed", Ev() == "start:PushToTalk,cancel" && swallowed.Contains("vEsc") || Ev() == "start:PushToTalk,cancel" && swallowed.Count == 2, Ev() + " / " + string.Join(" ", swallowed));

            // 2. Ctrl+Alt chosen: Ctrl+Win does nothing
            h = New("Ctrl+Alt");
            Dn(h, VK_LCONTROL); Dn(h, VK_LWIN); Hold(50); Up(h, VK_LWIN); Up(h, VK_LCONTROL);
            Dn(h, VK_LCONTROL); Dn(h, VK_LMENU); Hold(350); Up(h, VK_LMENU); Up(h, VK_LCONTROL);
            Check("Ctrl+Alt set: only Ctrl+Alt dictates", Ev() == "start:PushToTalk,finish", Ev());

            // 3. F9 alone: works and never reaches the app
            h = New("F9"); swallowed.Clear();
            Dn(h, 0x78); Hold(350); Up(h, 0x78);
            Check("F9 = push-to-talk", Ev() == "start:PushToTalk,finish", Ev());
            Check("F9 is swallowed (down and up)", swallowed.SequenceEqual(new[] { "vF9", "^F9" }), string.Join(" ", swallowed));
            h = New("F9"); Dn(h, VK_LSHIFT); Dn(h, 0x78); Up(h, 0x78); Up(h, VK_LSHIFT);
            Check("Shift+F9 does not trigger F9", events.Count == 0, Ev());

            // 4. Ctrl+Shift+Space: Space is part of the shortcut, hands-free by double tap
            h = New("Ctrl+Shift+Space"); swallowed.Clear();
            Dn(h, VK_LCONTROL); Dn(h, VK_LSHIFT); Dn(h, VK_SPACE); Hold(350); Up(h, VK_SPACE); Up(h, VK_LSHIFT); Up(h, VK_LCONTROL);
            Check("Ctrl+Shift+Space = push-to-talk", Ev() == "start:PushToTalk,finish", Ev());
            Check("Space of Ctrl+Shift+Space is swallowed", swallowed.SequenceEqual(new[] { "vSpace", "^Space" }), string.Join(" ", swallowed));
            h = New("Ctrl+Shift+Space"); Dn(h, VK_SPACE); Up(h, VK_SPACE);
            Check("Plain Space still types (not swallowed, nothing fires)", events.Count == 0, Ev());

            // 5. Ctrl+Alt+D (a letter with two modifiers)
            h = New("Ctrl+Alt+D"); swallowed.Clear();
            Dn(h, VK_LCONTROL); Dn(h, VK_LMENU); Hold(20); Dn(h, 'D'); Hold(350); Up(h, 'D'); Up(h, VK_LMENU); Up(h, VK_LCONTROL);
            Check("Ctrl+Alt+D = push-to-talk", Ev() == "start:PushToTalk,finish", Ev());
            Check("D of Ctrl+Alt+D is swallowed", swallowed.SequenceEqual(new[] { "vD", "^D" }), string.Join(" ", swallowed));

            // 6. Paste / copy last
            h = New("Ctrl+Win"); swallowed.Clear();
            Dn(h, VK_LMENU); Dn(h, VK_LSHIFT); Dn(h, 'Z'); Up(h, 'Z'); Dn(h, 'X'); Up(h, 'X'); Up(h, VK_LSHIFT); Up(h, VK_LMENU);
            Check("Alt+Shift+Z / X = paste / copy last", Ev() == "paste,copy", Ev());
            h = New("Ctrl+Win", paste: "Ctrl+Alt+V");
            Dn(h, VK_LMENU); Dn(h, VK_LSHIFT); Dn(h, 'Z'); Up(h, 'Z'); Up(h, VK_LSHIFT); Up(h, VK_LMENU);
            Dn(h, VK_LCONTROL); Dn(h, VK_LMENU); Dn(h, 'V'); Up(h, 'V'); Up(h, VK_LMENU); Up(h, VK_LCONTROL);
            Check("Custom paste Ctrl+Alt+V replaces Alt+Shift+Z", Ev() == "paste", Ev());
            // re-hear, and the "typed after the dictation" clock that guards scratch that / re-hear
            h = New("Ctrl+Win"); h.RehearRequested += () => events.Add("rehear"); swallowed.Clear();
            HotkeyService.LastUserKeyTick = 0;
            Dn(h, VK_LMENU); Dn(h, VK_LSHIFT); Dn(h, 'R'); Up(h, 'R'); Up(h, VK_LSHIFT); Up(h, VK_LMENU);
            Check("Alt+Shift+R = re-hear, and the R is swallowed", Ev() == "rehear" && swallowed.Contains("vR"), Ev() + " / " + string.Join(" ", swallowed));
            Check("A Vox shortcut does not count as typing", HotkeyService.LastUserKeyTick == 0, HotkeyService.LastUserKeyTick.ToString());
            Dn(h, 'A'); Up(h, 'A');
            Check("Typing a letter counts as typing", HotkeyService.LastUserKeyTick > 0, HotkeyService.LastUserKeyTick.ToString());
            long t0 = HotkeyService.LastUserKeyTick; Hold(20);
            Dn(h, VK_LSHIFT); Up(h, VK_LSHIFT);
            Check("A modifier alone does not count as typing", HotkeyService.LastUserKeyTick == t0, HotkeyService.LastUserKeyTick.ToString());
            h = New("Ctrl+Win"); Dn(h, VK_LCONTROL); Dn(h, 'Z'); Up(h, 'Z'); Up(h, VK_LCONTROL);
            Check("Ctrl+Z (undo) is left alone", events.Count == 0, Ev());

            // 7. Recording a shortcut
            h = New("Ctrl+Alt"); swallowed.Clear(); KeyCombo? got = null; bool done = false;
            h.BeginCapture(c => { got = c; done = true; });
            Dn(h, VK_LCONTROL); Dn(h, VK_LWIN); Up(h, VK_LWIN); Up(h, VK_LCONTROL);
            Check("Recording Ctrl, Win = Ctrl + Win", done && got?.Serialize() == "Ctrl+Win", got?.Serialize() ?? "null");
            Check("While recording every key is swallowed and nothing dictates", swallowed.Count == 4 && events.Count == 0, string.Join(" ", swallowed) + " / " + Ev());
            h = New("Ctrl+Win"); got = null; done = false;
            h.BeginCapture(c => { got = c; done = true; });
            Dn(h, VK_LCONTROL); Dn(h, VK_LSHIFT); Dn(h, VK_SPACE); Up(h, VK_LSHIFT); Up(h, VK_SPACE); Up(h, VK_LCONTROL);
            Check("Recording keeps the largest set held (Ctrl+Shift+Space, released in any order)", done && got?.Serialize() == "Ctrl+Shift+Space", got?.Serialize() ?? "null");
            h = New("Ctrl+Win"); got = KeyCombo.DefaultCopyLast; done = false;
            h.BeginCapture(c => { got = c; done = true; });
            Dn(h, VK_ESCAPE); Up(h, VK_ESCAPE);
            Check("Esc cancels recording", done && got == null);
            Dn(h, VK_LCONTROL); Dn(h, VK_LWIN); Hold(350); Up(h, VK_LWIN); Up(h, VK_LCONTROL);
            Check("After recording ends the shortcut works again", Ev() == "start:PushToTalk,finish", Ev());

            // 8. Validation and text form
            foreach (var (txt, ok) in new[] { ("Ctrl+Win", true), ("Ctrl+Alt", true), ("F9", true), ("Ctrl+Shift+Space", true), ("Alt+Shift+Z", true),
                                              ("Ctrl", false), ("Shift", false), ("A", false), ("Shift+A", false), ("Space", false), ("Ctrl+Escape", false), ("Win+F1", true) })
            {
                var c = KeyCombo.Parse(txt == "Ctrl+Escape" ? "Ctrl+Key27" : txt);
                Check($"\"{txt}\" {(ok ? "allowed" : "refused")}", c != null && (c.Problem() == null) == ok, c?.Problem() ?? "parse failed");
            }
            foreach (var txt in new[] { "Ctrl+Win", "Ctrl+Alt+;", "Ctrl+Shift+Space", "F12", "Win+Num5", "Alt+Shift+PageDown" })
                Check($"\"{txt}\" saves and loads the same", KeyCombo.Parse(txt)?.Serialize() == txt, KeyCombo.Parse(txt)?.Serialize() ?? "null");
            var legacy = new AppSettings { Hotkey = HotkeyPreset.CtrlAlt };
            Check("Old setting Hotkey=CtrlAlt still means Ctrl + Alt", legacy.DictationCombo.Serialize() == "Ctrl+Alt");
            legacy.DictationKeys = "Ctrl+Win";
            Check("New DictationKeys wins over the old setting", legacy.DictationCombo.Serialize() == "Ctrl+Win");
        }
        catch (Exception ex) { fail++; sb.AppendLine("FAILED: " + ex); }
        sb.Insert(0, $"{pass} passed, {fail} failed\n");
        File.WriteAllText(args[1], sb.ToString());
    }
}
