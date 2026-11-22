using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Vox.Core;

/// <summary>
/// "Accurate mode": Qwen3-ASR (or another transcribe.cpp GGUF model, *.gguf) run on the CPU through transcribe.cpp v0.4.1
/// (MIT). Its native files live in the "asr" folder next to Vox.exe. They ship their own ggml.dll, a different version
/// from the one in "engine" (Parakeet + the speech detector), and Windows can't hold two DLLs of the same name in one
/// program, so the model runs in a helper copy of Vox (Vox.exe --asr-host) that this class starts and talks to over a
/// pipe. Nothing touches the network. If the helper stops, it is started again on the next dictation.
/// Measured 2026-11-11 on 25 read-aloud test sentences (fair test): about 3 points more words right than Parakeet v3,
/// about 3 times slower on the i7-7700HQ.
/// </summary>
public sealed class QwenEngine : ISpeechEngine
{
    public static string AsrDir => Path.Combine(AppContext.BaseDirectory, "asr");
    public static bool IsAvailable => File.Exists(Path.Combine(AsrDir, "transcribe.dll"));
    public static bool IsGgufModel(string path) => path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase);

    Process? _host;
    BinaryReader? _in;
    BinaryWriter? _out;
    string _path = "";
    int _threads;
    readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>Dictionary words passed to the model as hints (transcribe.cpp vocabulary biasing). Empty = none.</summary>
    public IReadOnlyList<string> Hints { get; set; } = Array.Empty<string>();

    public string ModelName => string.IsNullOrEmpty(_path) ? "" : Path.GetFileNameWithoutExtension(_path);
    public bool IsLoaded => _host is { HasExited: false };

    public static int AutoThreads => Math.Clamp(Environment.ProcessorCount / 2, 2, 8);   // ~physical cores

    public async Task LoadAsync(string modelPath)
    {
        await _lock.WaitAsync();
        try
        {
            if (IsLoaded && string.Equals(_path, modelPath, StringComparison.OrdinalIgnoreCase)) return;
            Stop();
            if (!IsAvailable) throw new FileNotFoundException("The accurate-mode engine files are missing (expected in: " + AsrDir + ").");
            _path = modelPath;
            await Task.Run(() => Start(AutoThreads));
        }
        finally { _lock.Release(); }
    }

    void Start(int threads)
    {
        var sw = Stopwatch.StartNew();
        var psi = new ProcessStartInfo(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Vox.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = false,
        };
        foreach (var a in new[] { "--asr-host", AsrDir, _path, threads.ToString() }) psi.ArgumentList.Add(a);
        var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start the accurate-mode helper.");
        try { p.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
        _in = new BinaryReader(p.StandardOutput.BaseStream, Encoding.UTF8);
        _out = new BinaryWriter(p.StandardInput.BaseStream, Encoding.UTF8);
        string hello;
        try { hello = _in.ReadString(); }
        catch (EndOfStreamException) { throw new InvalidOperationException("The accurate-mode helper stopped while loading " + Path.GetFileName(_path) + "."); }
        if (!hello.StartsWith("ok|")) { try { p.Kill(); } catch { } throw new InvalidOperationException("Accurate mode could not load the model: " + hello.Split('|', 2).Last()); }
        _host = p; _threads = threads;
        Log.Info($"Accurate-mode model loaded: {Path.GetFileName(_path)} ({hello[3..]}) in {sw.ElapsedMilliseconds} ms, {threads} threads");
    }

    public async Task<string> TranscribeAsync(float[] samples, string language, int threads, string initialPrompt, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_path)) throw new InvalidOperationException("No speech model is loaded.");
        await _lock.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                int want = threads > 0 ? threads : AutoThreads;
                if (!IsLoaded || want != _threads) { if (IsLoaded) Stop(); Log.Info("accurate mode: starting the helper"); Start(want); }
                // Qwen3 can be told the language; "auto" means English here (it stops stray foreign words, and the
                // accuracy test was measured that way)
                var lang = string.IsNullOrWhiteSpace(language) || language == "auto" ? "en" : language;
                var hints = Hints;
                _out!.Write(samples.Length);
                _out.Write(lang);
                _out.Write(hints.Count);
                foreach (var h in hints) _out.Write(h);
                var bytes = MemoryMarshal.AsBytes(samples.AsSpan());
                _out.Write(bytes);
                _out.Flush();
                string reply;
                try { reply = _in!.ReadString(); }
                catch (EndOfStreamException) { Stop(); throw new InvalidOperationException("The accurate-mode helper stopped during a dictation."); }
                if (reply.StartsWith("ok|")) return reply[3..];
                throw new InvalidOperationException("Accurate mode failed: " + reply.Split('|', 2).Last());
            }, ct);
        }
        finally { _lock.Release(); }
    }

    void Stop()
    {
        try { _out?.Write(-1); _out?.Flush(); } catch { }
        try { if (_host is { HasExited: false } h && !h.WaitForExit(1500)) h.Kill(); } catch { }
        _host?.Dispose(); _host = null; _in = null; _out = null;
    }

    public void Dispose() { Stop(); }

    // ================= helper side (runs inside "Vox.exe --asr-host asrDir model.gguf threads") =================

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FnInt();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr FnPtr();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr FnPtrInt(int a);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr FnPtrPtr(IntPtr a);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void FnVoidPtr(IntPtr a);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FnOpen(byte[] path, IntPtr load, IntPtr session, out IntPtr outSession);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FnRun(IntPtr s, float[] pcm, int n, IntPtr run);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetDllDirectory(string path);

    public static void RunHost(string[] args)
    {
        string dir = args[1], model = args[2];
        int threads = int.Parse(args[3]);
        var stdout = new BinaryWriter(Console.OpenStandardOutput(), Encoding.UTF8);
        var stdin = new BinaryReader(Console.OpenStandardInput(), Encoding.UTF8);
        try
        {
            SetDllDirectory(dir);
            var lib = NativeLibrary.Load(Path.Combine(dir, "transcribe.dll"));
            T F<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, name));
            var status = F<FnPtrInt>("transcribe_status_string");
            string S(IntPtr p) => p == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(p) ?? "";
            F<FnInt>("transcribe_init_backends_default")();
            IntPtr load = Zeroed(256), sess = Zeroed(256), run = Zeroed(256);
            F<FnVoidPtr>("transcribe_model_load_params_init")(load);
            F<FnVoidPtr>("transcribe_session_params_init")(sess);
            F<FnVoidPtr>("transcribe_run_params_init")(run);
            long ls = Marshal.ReadInt64(load), ss = Marshal.ReadInt64(sess), rs = Marshal.ReadInt64(run);
            if (ls != 32 || ss != 24 || rs != 104) throw new InvalidOperationException($"engine version mismatch (struct sizes {ls}/{ss}/{rs})");
            Marshal.WriteInt32(load, 8, 1);          // CPU backend (what was measured; the GPU path is untested)
            Marshal.WriteInt32(sess, 8, threads);
            int st = F<FnOpen>("transcribe_open")(Encoding.UTF8.GetBytes(model + "\0"), load, sess, out var session);
            if (st != 0) throw new InvalidOperationException(S(status(st)));
            var m = F<FnPtrPtr>("transcribe_get_model")(session);
            var arch = S(F<FnPtrPtr>("transcribe_model_arch_string")(m));
            var runFn = F<FnRun>("transcribe_run");
            var text = F<FnPtrPtr>("transcribe_full_text");
            runFn(session, new float[16000], 16000, run);   // warm-up so the first dictation isn't slow
            stdout.Write("ok|" + arch); stdout.Flush();

            var langPtrs = new Dictionary<string, IntPtr>();
            string[] lastHints = Array.Empty<string>();
            IntPtr[] hintPtrs = Array.Empty<IntPtr>();
            IntPtr hintArr = IntPtr.Zero;
            while (true)
            {
                int n;
                try { n = stdin.ReadInt32(); } catch (EndOfStreamException) { break; }   // Vox closed: quit with it
                if (n < 0) break;
                var lang = stdin.ReadString();
                int nh = stdin.ReadInt32();
                var hints = new string[nh];
                for (int i = 0; i < nh; i++) hints[i] = stdin.ReadString();
                if (!hints.SequenceEqual(lastHints))
                {
                    foreach (var p in hintPtrs) Marshal.FreeCoTaskMem(p);
                    if (hintArr != IntPtr.Zero) Marshal.FreeHGlobal(hintArr);
                    hintPtrs = hints.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
                    hintArr = hintPtrs.Length == 0 ? IntPtr.Zero : Marshal.AllocHGlobal(IntPtr.Size * hintPtrs.Length);
                    for (int i = 0; i < hintPtrs.Length; i++) Marshal.WriteIntPtr(hintArr, i * IntPtr.Size, hintPtrs[i]);
                    Marshal.WriteIntPtr(run, 72, hintArr);          // run params: vocabulary terms
                    Marshal.WriteInt32(run, 80, hintPtrs.Length);   // and their count
                    lastHints = hints;
                }
                var pcm = new float[n];
                var span = MemoryMarshal.AsBytes(pcm.AsSpan());
                int got = 0;
                while (got < span.Length) { int r = stdin.BaseStream.Read(span[got..]); if (r <= 0) return; got += r; }
                if (!langPtrs.TryGetValue(lang, out var lp)) langPtrs[lang] = lp = Marshal.StringToCoTaskMemUTF8(lang);
                Marshal.WriteIntPtr(run, 32, lp);
                st = n == 0 ? 0 : runFn(session, pcm, n, run);
                var reply = st != 0 ? "error|" + S(status(st)) : n == 0 ? "ok|" : "ok|" + S(text(session)).Replace('\n', ' ').Trim();
                stdout.Write(reply); stdout.Flush();
            }
            F<FnVoidPtr>("transcribe_session_free")(session);
        }
        catch (Exception ex)
        {
            try { stdout.Write("error|" + ex.Message); stdout.Flush(); } catch { }
        }
    }

    static IntPtr Zeroed(int n) { var p = Marshal.AllocHGlobal(n); for (int i = 0; i < n; i++) Marshal.WriteByte(p, i, 0); return p; }
}
