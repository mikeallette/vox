using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Vox.Core;

/// <summary>
/// NVIDIA Parakeet TDT (via whisper.cpp's parakeet library) running locally on the CPU. The model stays loaded in
/// memory between dictations. Native DLLs live in the "engine" folder next to Vox.exe. Nothing touches the network.
/// Note: Parakeet has no "initial prompt", so the dictionary can only correct text after the fact.
/// </summary>
public sealed class ParakeetEngine : ISpeechEngine
{
    // ---- native layout (include/parakeet.h, whisper.cpp v1.9.2) ----
    [StructLayout(LayoutKind.Sequential)]
    struct ContextParams { public byte UseGpu; public int GpuDevice; }

    [StructLayout(LayoutKind.Sequential)]
    struct FullParams
    {
        public int Strategy, NThreads, OffsetMs, DurationMs;
        public byte NoContext;
        public int AudioCtx;
        public IntPtr NewSegmentCb, NewSegmentUd, NewTokenCb, NewTokenUd, ProgressCb, ProgressUd, EncoderBeginCb, EncoderBeginUd, AbortCb, AbortUd;
    }

    const string Parakeet = "parakeet", Ggml = "ggml";

    [DllImport(Ggml, CallingConvention = CallingConvention.Cdecl)]
    static extern void ggml_backend_load_all_from_path([MarshalAs(UnmanagedType.LPUTF8Str)] string dir);

    [DllImport(Parakeet, CallingConvention = CallingConvention.Cdecl)] static extern ContextParams parakeet_context_default_params();
    [DllImport(Parakeet, CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr parakeet_init_from_file_with_params([MarshalAs(UnmanagedType.LPUTF8Str)] string path, ContextParams p);
    [DllImport(Parakeet, CallingConvention = CallingConvention.Cdecl)] static extern FullParams parakeet_full_default_params(int strategy);
    [DllImport(Parakeet, CallingConvention = CallingConvention.Cdecl)] static extern int parakeet_full(IntPtr ctx, FullParams p, float[] samples, int n);
    [DllImport(Parakeet, CallingConvention = CallingConvention.Cdecl)] static extern int parakeet_full_n_segments(IntPtr ctx);
    [DllImport(Parakeet, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr parakeet_full_get_segment_text(IntPtr ctx, int i);
    [DllImport(Parakeet, CallingConvention = CallingConvention.Cdecl)] static extern int parakeet_full_n_tokens(IntPtr ctx, int seg);
    [DllImport(Parakeet, CallingConvention = CallingConvention.Cdecl)] static extern float parakeet_full_get_token_p(IntPtr ctx, int seg, int tok);
    [DllImport(Parakeet, CallingConvention = CallingConvention.Cdecl)] static extern void parakeet_free(IntPtr ctx);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetDllDirectory(string path);

    static bool _resolverSet, _backendsLoaded;
    static readonly object InitLock = new();
    public static string EngineDir => Path.Combine(AppContext.BaseDirectory, "engine");
    public static bool IsAvailable => File.Exists(Path.Combine(EngineDir, "parakeet.dll")) && File.Exists(Path.Combine(EngineDir, "ggml.dll"));

    IntPtr _ctx;
    readonly SemaphoreSlim _lock = new(1, 1);
    string _path = "";

    public string ModelName => string.IsNullOrEmpty(_path) ? "" : Path.GetFileNameWithoutExtension(_path);
    public bool IsLoaded => _ctx != IntPtr.Zero;

    static void EnsureNative()
    {
        lock (InitLock)
        {
            if (!_resolverSet)
            {
                if (!IsAvailable) throw new FileNotFoundException("The Parakeet engine files are missing (expected in: " + EngineDir + ").");
                SetDllDirectory(EngineDir);   // lets ggml-base.dll and the Visual C++ runtime next to it resolve
                NativeLibrary.SetDllImportResolver(typeof(ParakeetEngine).Assembly, (name, asm, _) =>
                    name is Parakeet or Ggml ? NativeLibrary.Load(Path.Combine(EngineDir, name + ".dll")) : IntPtr.Zero);
                _resolverSet = true;
            }
            if (!_backendsLoaded) { ggml_backend_load_all_from_path(EngineDir); _backendsLoaded = true; }
        }
    }

    /// <summary>Loads the native libraries and backends without a model (used by --sysinfo to prove the engine can start).</summary>
    public static void CheckNative() => EnsureNative();

    public async Task LoadAsync(string modelPath)
    {
        await _lock.WaitAsync();
        try
        {
            if (_ctx != IntPtr.Zero && string.Equals(_path, modelPath, StringComparison.OrdinalIgnoreCase)) return;
            Unload();
            var sw = Stopwatch.StartNew();
            _ctx = await Task.Run(() =>
            {
                EnsureNative();
                var cp = parakeet_context_default_params();
                cp.UseGpu = 0;
                return parakeet_init_from_file_with_params(modelPath, cp);
            });
            if (_ctx == IntPtr.Zero) throw new InvalidOperationException("Parakeet could not load the model file: " + Path.GetFileName(modelPath));
            _path = modelPath;
            Log.Info($"Parakeet model loaded: {Path.GetFileName(modelPath)} in {sw.ElapsedMilliseconds} ms");
        }
        finally { _lock.Release(); }
    }

    public async Task<string> TranscribeAsync(float[] samples, string language, int threads, string initialPrompt, CancellationToken ct = default)
        => (await TranscribeDetailedAsync(samples, threads, ct)).text;

    /// <summary>Text plus the mean token probability (0..1), used to spot words made up from noise.</summary>
    public async Task<(string text, float confidence)> TranscribeDetailedAsync(float[] samples, int threads, CancellationToken ct = default)
    {
        if (_ctx == IntPtr.Zero) throw new InvalidOperationException("No speech model is loaded.");
        await _lock.WaitAsync(ct);
        try
        {
            var ctx = _ctx;
            int nThreads = threads > 0 ? threads : Math.Clamp(Environment.ProcessorCount / 2, 2, 8);   // ~physical cores
            return await Task.Run(() =>
            {
                var p = parakeet_full_default_params(0);   // greedy
                p.NThreads = nThreads;
                int rc = parakeet_full(ctx, p, samples, samples.Length);
                if (rc != 0) throw new InvalidOperationException("Parakeet failed with code " + rc);
                int n = parakeet_full_n_segments(ctx);
                var sb = new System.Text.StringBuilder();
                double pSum = 0; int pN = 0;
                for (int i = 0; i < n; i++)
                {
                    var t = Marshal.PtrToStringUTF8(parakeet_full_get_segment_text(ctx, i));
                    if (!string.IsNullOrWhiteSpace(t)) { if (sb.Length > 0) sb.Append(' '); sb.Append(t.Trim()); }
                    int nt = parakeet_full_n_tokens(ctx, i);
                    for (int k = 0; k < nt; k++) { float pk = parakeet_full_get_token_p(ctx, i, k); if (pk >= 0 && pk <= 1) { pSum += pk; pN++; } }
                }
                return (sb.ToString(), pN > 0 ? (float)(pSum / pN) : -1f);
            }, ct);
        }
        finally { _lock.Release(); }
    }

    void Unload()
    {
        if (_ctx != IntPtr.Zero) { parakeet_free(_ctx); _ctx = IntPtr.Zero; _path = ""; }
    }

    public void Dispose() { Unload(); }
}
