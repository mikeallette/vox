using System.Text;

namespace Vox.Core;

/// <summary>Vox.exe --feature-tests out.md : checks for the calculator, voice actions, teach-a-word and action items.</summary>
internal static partial class Trainer
{
    public static void FeatureTests(string[] args)
    {
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string group, string input, string want, string got)
        {
            bool ok = want == got;
            if (ok) pass++; else fail++;
            if (!ok) sb.Append($"| {group} | {input} | {want} | **{got}** |\n");
        }
        string Calc(string s) => VoiceActions.TryCalculate(s, out var r) ? r : "(none)";
        Check("calc", "Calculate 400 divided by 650 as a percent.", "61.54%", Calc("Calculate 400 divided by 650 as a percent."));
        Check("calc", "calculate four hundred over six hundred fifty as a percent", "61.54%", Calc("calculate four hundred over six hundred fifty as a percent"));
        Check("calc", "Calculate 2 plus 3 times 4.", "14", Calc("Calculate 2 plus 3 times 4."));
        Check("calc", "Compute 62 percent of 650.", "403", Calc("Compute 62 percent of 650."));
        Check("calc", "Calculate 1.8 squared.", "3.24", Calc("Calculate 1.8 squared."));
        Check("calc", "Calculate the square root of 2.", "1.414", Calc("Calculate the square root of 2."));
        Check("calc", "Calculate 400 V over 650 V as a percentage.", "61.54%", Calc("Calculate 400 V over 650 V as a percentage."));
        Check("calc", "Calculate twelve point five times eight.", "100", Calc("Calculate twelve point five times eight."));
        Check("calc", "Calculate the cost of the project.", "(none)", Calc("Calculate the cost of the project."));
        Check("calc", "What is 2 plus 2?", "(none)", Calc("What is 2 plus 2?"));
        Check("calc", "Calculate 10 divided by 0.", "(none)", Calc("Calculate 10 divided by 0."));
        Check("calc", "Calculate 15 percent.", "0.15", Calc("Calculate 15 percent."));
        Check("calc", "Calculate 3 over 4 in percent.", "75%", Calc("Calculate 3 over 4 in percent."));
        Check("calc", "Calculate 3 over 4 percent.", "75%", Calc("Calculate 3 over 4 percent."));

        Check("scratch", "Scratch that.", "True", VoiceActions.IsScratchThat("Scratch that.").ToString());
        Check("scratch", "Please scratch that idea.", "False", VoiceActions.IsScratchThat("Please scratch that idea.").ToString());
        string Enter(string s) => VoiceActions.SplitPressEnter(s, out var r) ? "enter: " + r : "no";
        Check("enter", "Sounds good, see you then, press enter.", "enter: Sounds good, see you then", Enter("Sounds good, see you then, press enter."));
        Check("enter", "Press enter.", "enter: ", Enter("Press enter."));
        Check("enter", "Hit enter", "enter: ", Enter("Hit enter"));
        Check("enter", "I'll press enter when it's ready.", "no", Enter("I'll press enter when it's ready."));
        Check("enter", "I'll send it.", "no", Enter("I'll send it."));
        string NoteOf(string s) => VoiceActions.TryNoteToSelf(s, out var n) ? n : "(none)";
        Check("note", "Note to self, call the supplier about the inductors.", "Call the supplier about the inductors.", NoteOf("Note to self, call the supplier about the inductors."));
        Check("note", "Note to self.", "(none)", NoteOf("Note to self."));
        Check("note", "I made a note to self yesterday.", "(none)", NoteOf("I made a note to self yesterday."));

        string Teach(string a, string b) => string.Join("; ", TeachWords.Propose(a, b).Select(e => e.From + " -> " + e.To));
        Check("teach", "Bryan has a dentist appointment", "Bryan -> Brian", Teach("Bryan has a dentist appointment on Tuesday.", "Brian has a dentist appointment on Tuesday."));
        Check("teach", "the flyback snuffer", "snuffer -> snubber", Teach("The flyback snuffer is hot.", "The flyback snubber is hot."));
        Check("teach", "update the bomb", "bomb -> BOM", Teach("Please update the bomb today.", "Please update the BOM today."));
        Check("teach", "a -> the is skipped", "", Teach("Put a ceramic cap on it.", "Put the ceramic cap on it."));
        Check("teach", "missing word added", "LLC on -> LLC tank on", Teach("Check the LLC on the scope.", "Check the LLC tank on the scope."));
        Check("teach", "removed word only", "", Teach("I really think so.", "I think so."));
        Check("teach", "punctuation only", "", Teach("Yes, it works.", "Yes it works!"));
        Check("teach", "two changes", "LSE -> LLC; derailing -> derating", Teach("The LSE tank and the derailing sheet.", "The LLC tank and the derating sheet."));

        var lines = new List<(double, string, string)>
        {
            (10, "You", "Thanks everyone for joining. I'll send the updated BOM by Friday."),
            (40, "Speaker 1", "Can you check the snubber values on the second board? Let me know if you can hear me."),
            (70, "Speaker 2", "The ripple looked fine yesterday."),
            (95, "Speaker 1", "We need to order more inductors."),
        };
        var acts = ActionItems.Find(lines).Select(a => a.text).ToList();
        Check("actions", "count", "3", acts.Count.ToString());
        Check("actions", "first", "I'll send the updated BOM by Friday.", acts.ElementAtOrDefault(0) ?? "");
        Check("actions", "not 'let me know if you can hear me'", "False", acts.Any(a => a.Contains("hear me")).ToString());

        // export on one "PC", import on another (two temporary data folders; the real settings file is never touched)
        var tmp = Path.Combine(Path.GetTempPath(), "vox-transfer-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var home = new DataStore(Path.Combine(tmp, "home"));
            home.Dictionary.Add(new DictEntry { From = "snuffer", To = "snubber" });
            home.Dictionary.Add(new DictEntry { From = "bomb", To = "BOM" });
            home.Snippets.Add(new Snippet { Trigger = "my sig", Text = "Thanks,\nMe" });
            var homeSet = new AppSettings { RemoveFillers = false, RehearKeys = "Ctrl+Alt+R", MicrophoneName = "Home mic", AccurateApps = new() { "WINWORD" } };
            var file = Path.Combine(tmp, "export.json");
            SettingsTransfer.Export(file, homeSet, home);
            var work = new DataStore(Path.Combine(tmp, "work"));
            work.Dictionary.Add(new DictEntry { From = "bomb", To = "bill of materials" });   // a clash: work keeps its own
            var workSet = new AppSettings { MicrophoneName = "Work headset" };
            var summary = SettingsTransfer.Import(file, workSet, work, saveSettings: false);
            Check("transfer", "dictionary added, clash kept", "snubber|bill of materials|2",
                $"{work.Dictionary.FirstOrDefault(d => d.From == "snuffer")?.To}|{work.Dictionary.First(d => d.From == "bomb").To}|{work.Dictionary.Count}");
            Check("transfer", "snippet added with its line break", "Thanks,\nMe", work.Snippets.FirstOrDefault()?.Text ?? "");
            Check("transfer", "settings carried over", "False|Ctrl+Alt+R|WINWORD", $"{workSet.RemoveFillers}|{workSet.RehearKeys}|{string.Join(",", workSet.AccurateApps)}");
            Check("transfer", "machine settings stay", "Work headset", workSet.MicrophoneName);
            Check("transfer", "summary says what happened", "True", summary.StartsWith("Added 1 dictionary entries (0 already here, 1 kept").ToString());
        }
        catch (Exception ex) { Check("transfer", "round trip", "no error", ex.GetType().Name + ": " + ex.Message); }
        finally { try { Directory.Delete(tmp, true); } catch { } }

        var md = $"# Feature tests: {pass} of {pass + fail} pass\n\n" + (fail == 0 ? "No failures.\n" : "| Group | Input | Want | Got |\n|---|---|---|---|\n" + sb);
        File.WriteAllText(args[1], md, new UTF8Encoding(false));
    }
}
