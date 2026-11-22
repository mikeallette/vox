using System.Diagnostics;
using System.Text;

namespace Vox.Core;

/// <summary>
/// Vox.exe --model-bench outDir refs.tsv wavDir model.bin threads
/// Fair model comparison helper. For each recording it runs Vox's own engine three ways, with no prompt, no dictionary
/// and no text cleanup: (a) Vox's path (speech gate trim + conditioning), (b) whole clip conditioned, (c) whole clip raw.
/// It also writes the exact audio Vox's path feeds the engine to outDir\prepped\{id}.wav, so other models can be given
/// the identical input. Output: outDir\v3-gated.tsv, v3-cond.tsv, v3-raw.tsv (id TAB text TAB ms).
/// </summary>
internal static partial class Trainer
{
    public static async Task ModelBenchAsync(string[] args)
    {
        string outDir = args[1], refsPath = args[2], wavDir = args[3], model = args[4];
        int threads = int.Parse(args[5]);
        Directory.CreateDirectory(Path.Combine(outDir, "prepped"));
        var ids = File.ReadLines(refsPath).Select(l => l.Split('\t')[0].Trim().TrimStart('﻿'))
                      .Where(i => i.Length > 0 && File.Exists(Path.Combine(wavDir, i + ".wav"))).ToList();
        using var engine = new SpeechEngine();
        await engine.LoadAsync(model);
        await engine.TranscribeAsync(new float[16000], "en", threads, "");
        var outs = new[] { new StringBuilder(), new StringBuilder(), new StringBuilder() };
        foreach (var id in ids)
        {
            var x = TrainAudio.Load(Path.Combine(wavDir, id + ".wav"));
            var s = SpeechGate.Analyze(x);
            var gated = AudioPrep.Condition(SpeechGate.Trim(x, s));
            WriteWav(Path.Combine(outDir, "prepped", id + ".wav"), gated);
            var inputs = new[] { gated, AudioPrep.Condition(x), x };
            for (int k = 0; k < 3; k++)
            {
                var sw = Stopwatch.StartNew();
                var t = await engine.TranscribeAsync(inputs[k], "en", threads, "");
                outs[k].Append($"{id}\t{t.Replace('\t', ' ').Replace('\n', ' ').Trim()}\t{sw.ElapsedMilliseconds}\n");
            }
        }
        var names = new[] { "v3-gated", "v3-cond", "v3-raw" };
        for (int k = 0; k < 3; k++) File.WriteAllText(Path.Combine(outDir, names[k] + ".tsv"), outs[k].ToString(), new UTF8Encoding(false));
    }

    static void WriteWav(string path, float[] x)
    {
        using var bw = new BinaryWriter(File.Create(path));
        int bytes = x.Length * 2;
        bw.Write(Encoding.ASCII.GetBytes("RIFF")); bw.Write(36 + bytes); bw.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        bw.Write(16); bw.Write((short)1); bw.Write((short)1); bw.Write(16000); bw.Write(32000); bw.Write((short)2); bw.Write((short)16);
        bw.Write(Encoding.ASCII.GetBytes("data")); bw.Write(bytes);
        foreach (var v in x) bw.Write((short)Math.Clamp(Math.Round(v * 32767.0), -32768, 32767));
    }
}
