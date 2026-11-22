using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace Vox.Core;

/// <summary>
/// "Vox.exe --sysinfo": a short, privacy-safe summary of this PC (no user names, no file paths, no network) so the
/// right speech model can be chosen. Shown in a window, copied to the clipboard and saved as vox-sysinfo.txt in %TEMP%.
/// </summary>
internal static class SysInfo
{
    public static string Build()
    {
        var sb = new StringBuilder();
        sb.AppendLine("VOX SYSTEM CHECK");
        sb.AppendLine("Vox " + (typeof(SysInfo).Assembly.GetName().Version?.ToString(3) ?? "?"));
        sb.AppendLine("Windows: " + RuntimeInformation.OSDescription + "  (" + RuntimeInformation.OSArchitecture + ")");

        string cpu = "?";
        try { cpu = (Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", "?") as string ?? "?").Trim(); } catch { }
        sb.AppendLine("CPU: " + cpu);
        sb.AppendLine("Logical processors: " + Environment.ProcessorCount);
        sb.AppendLine($"CPU features: AVX={Avx.IsSupported}  AVX2={Avx2.IsSupported}  FMA={Fma.IsSupported}  AVX-512={Avx512F.IsSupported}");
        sb.AppendLine("  (AVX2 = yes means the fast Parakeet engine runs at full speed)");

        try { sb.AppendLine($"Memory: {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0:0.0} GB total"); } catch { }

        try
        {
            var mics = MicRecorder.Devices();
            sb.AppendLine($"Microphones ({mics.Count}): " + (mics.Count == 0 ? "none found" : string.Join("; ", mics)));
        }
        catch (Exception ex) { sb.AppendLine("Microphones: error " + ex.Message); }
        try
        {
            using var en = new MMDeviceEnumerator();
            sb.AppendLine("Playback device (meeting audio is captured from here): " + en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).FriendlyName);
        }
        catch { sb.AppendLine("Playback device: none found (meeting 'Others' audio would be unavailable)"); }

        sb.AppendLine("Speech engine files present: " + ParakeetEngine.IsAvailable);
        if (ParakeetEngine.IsAvailable)
        {
            try { ParakeetEngine.CheckNative(); sb.AppendLine("Speech engine can load (Visual C++ runtime OK): yes"); }
            catch (Exception ex) { sb.AppendLine("Speech engine can load: NO  (" + ex.GetType().Name + ": " + ex.Message + ")"); }
        }
        var models = new List<string>();
        foreach (var dir in new[] { Path.Combine(AppContext.BaseDirectory, "models") })
            if (Directory.Exists(dir)) foreach (var f in Directory.GetFiles(dir, "ggml-*.bin")) models.Add($"{Path.GetFileName(f)} ({new FileInfo(f).Length / 1048576} MB)");
        sb.AppendLine("Models next to the app: " + (models.Count == 0 ? "none" : string.Join("; ", models)));
        sb.AppendLine(".NET runtime: " + RuntimeInformation.FrameworkDescription);
        return sb.ToString();
    }
}
