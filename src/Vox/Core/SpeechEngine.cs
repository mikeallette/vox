namespace Vox.Core;

public interface ISpeechEngine : IDisposable
{
    string ModelName { get; }
    bool IsLoaded { get; }
    Task LoadAsync(string modelPath);
    Task<string> TranscribeAsync(float[] samples, string language, int threads, string initialPrompt, CancellationToken ct = default);
}

/// <summary>
/// Picks the speech engine from the model file: ggml-parakeet-*.bin runs on the fast native Parakeet engine,
/// every other ggml-*.bin is a Whisper model. The rest of Vox only talks to this class.
/// </summary>
public sealed class SpeechEngine : ISpeechEngine
{
    ISpeechEngine? _active;
    readonly WhisperEngine _whisper = new();
    readonly ParakeetEngine _parakeet = new();
    readonly QwenEngine _qwen = new();

    public string ModelName => _active?.ModelName ?? "";
    public bool IsLoaded => _active?.IsLoaded == true || (_active == _qwen && _qwen.ModelName != "");
    public string EngineName => _active == _parakeet ? "Parakeet" : _active == _whisper ? "Whisper" : _active == _qwen ? "Accurate" : "";

    /// <summary>
    /// Accurate mode (Qwen3) hears better from the whole clip: the speech-gate trim cost it 5 to 9 errors in the
    /// 2026-11-11 fair test, while it helped Parakeet slightly. The gate still skips clips with no speech.
    /// </summary>
    public bool WantsWholeClip => _active == _qwen;

    /// <summary>Words accurate mode should listen for (from the dictionary); the other engines ignore them.</summary>
    public IReadOnlyList<string> Hints { get => _qwen.Hints; set => _qwen.Hints = value; }

    public static bool IsParakeetModel(string path) => Path.GetFileName(path).Contains("parakeet", StringComparison.OrdinalIgnoreCase)
                                                       && !QwenEngine.IsGgufModel(path);

    public async Task LoadAsync(string modelPath)
    {
        ISpeechEngine next = QwenEngine.IsGgufModel(modelPath) ? _qwen : IsParakeetModel(modelPath) ? _parakeet : _whisper;
        await next.LoadAsync(modelPath);
        _active = next;
        SpeechGate.Init(modelPath);
    }

    /// <summary>Text plus mean token confidence (0..1), or -1 when the engine doesn't report it (Whisper).</summary>
    public async Task<(string text, float confidence)> TranscribeDetailedAsync(float[] samples, string language, int threads, string initialPrompt, CancellationToken ct = default)
    {
        if (_active == _parakeet) return await _parakeet.TranscribeDetailedAsync(samples, threads, ct);
        return (await TranscribeAsync(samples, language, threads, initialPrompt, ct), -1f);
    }

    public Task<string> TranscribeAsync(float[] samples, string language, int threads, string initialPrompt, CancellationToken ct = default)
        => (_active ?? throw new InvalidOperationException("No speech model is loaded.")).TranscribeAsync(samples, language, threads, initialPrompt, ct);

    public void Dispose() { _whisper.Dispose(); _parakeet.Dispose(); _qwen.Dispose(); }
}
