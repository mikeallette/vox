using System.Text;

namespace Vox.Core;

/// <summary>
/// Vox.exe --level-test outFile typical file1.wav|folder [more ...]
/// Plays each recording through LevelWatch in 40 ms blocks, as the microphone would, and reports whether and when the
/// "mic is very quiet" warning would show. typical = the user's usual speech level (0 = the built-in default).
/// </summary>
internal static partial class Trainer
{
    public static void LevelTest(string[] args)
    {
        float typical = float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
        if (typical <= 0) typical = LevelWatch.DefaultTypical;
        var files = args.Skip(3).SelectMany(a => Directory.Exists(a) ? Directory.GetFiles(a, "*.wav", SearchOption.AllDirectories) : new[] { a })
                        .Where(File.Exists).OrderBy(f => f).ToList();
        var md = new StringBuilder($"# Quiet-mic warning test (usual level {typical:0.0000})\n\n| Recording | Seconds | Speech level | Warned | First warning at | Warning at the end |\n|---|---:|---:|---|---:|---|\n");
        int warnedEnd = 0;
        foreach (var f in files)
        {
            var x = TrainAudio.Load(f);
            var w = new LevelWatch(typical);
            double? first = null;
            for (int i = 0; i + 640 <= x.Length; i += 640)
            {
                double sum = 0; for (int k = i; k < i + 640; k++) sum += x[k] * x[k];
                if (w.Push((float)Math.Sqrt(sum / 640)) && w.IsLow && first == null) first = (i + 640) / 16000.0;
            }
            bool end = w.SpeechLevel() < w.Threshold && x.Length >= 16000;
            if (end) warnedEnd++;
            md.Append($"| {Path.GetFileNameWithoutExtension(f)} | {x.Length / 16000.0:0.0} | {w.SpeechLevel():0.0000} | {(first != null ? "yes" : "no")} | {(first is double t ? $"{t:0.0} s" : "")} | {(end ? "yes" : "no")} |\n");
        }
        md.Append($"\nThreshold {new LevelWatch(typical).Threshold:0.0000}. Warned at the end: {warnedEnd} of {files.Count}.\n");
        File.WriteAllText(args[1], md.ToString(), new UTF8Encoding(false));
    }
}
