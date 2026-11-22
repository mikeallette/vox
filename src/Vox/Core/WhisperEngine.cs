using System.Diagnostics;
using Whisper.net;

namespace Vox.Core;

/// <summary>
/// Local speech-to-text via whisper.cpp (through Whisper.net). Nothing here touches the network:
/// the model is a file on disk, inference runs on the CPU of this PC.
/// </summary>
public sealed class WhisperEngine : ISpeechEngine
{
    WhisperFactory? _factory;
    readonly SemaphoreSlim _lock = new(1, 1);
    string _loadedPath = "";

    public string ModelName => string.IsNullOrEmpty(_loadedPath) ? "" : Path.GetFileNameWithoutExtension(_loadedPath);
    public bool IsLoaded => _factory != null;

    public async Task LoadAsync(string modelPath)
    {
        await _lock.WaitAsync();
        try
        {
            if (_factory != null && string.Equals(_loadedPath, modelPath, StringComparison.OrdinalIgnoreCase)) return;
            _factory?.Dispose(); _factory = null; _loadedPath = "";
            var sw = Stopwatch.StartNew();
            _factory = await Task.Run(() => WhisperFactory.FromPath(modelPath));
            _loadedPath = modelPath;
            Log.Info($"Model loaded: {Path.GetFileName(modelPath)} in {sw.ElapsedMilliseconds} ms");
        }
        finally { _lock.Release(); }
    }

    public async Task<string> TranscribeAsync(float[] samples, string language, int threads, string initialPrompt, CancellationToken ct = default)
    {
        if (_factory == null) throw new InvalidOperationException("No speech model is loaded.");
        await _lock.WaitAsync(ct);
        try
        {
            var lang = _loadedPath.Contains(".en", StringComparison.OrdinalIgnoreCase) ? "en" : language;
            var b = _factory.CreateBuilder()
                .WithLanguage(string.IsNullOrWhiteSpace(lang) ? "auto" : lang)
                .WithThreads(threads > 0 ? threads : Math.Max(2, Environment.ProcessorCount / 2 + 1));
            if (!string.IsNullOrWhiteSpace(initialPrompt)) b = b.WithPrompt(initialPrompt);
            await using var processor = b.Build();

            var sb = new System.Text.StringBuilder();
            await foreach (var seg in processor.ProcessAsync(samples, ct))
                sb.Append(seg.Text);
            return sb.ToString().Trim();
        }
        finally { _lock.Release(); }
    }

    public void Dispose() { _factory?.Dispose(); _factory = null; }
}
