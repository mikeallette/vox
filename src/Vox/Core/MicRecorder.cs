using NAudio.Wave;

namespace Vox.Core;

/// <summary>Records the microphone as 16 kHz mono PCM (what Whisper expects) and reports live input level.</summary>
public sealed class MicRecorder : IDisposable
{
    public const int SampleRate = 16000;
    public event Action<float>? Level;      // 0..1, ~25 Hz
    public event Action<float[]>? Chunk;    // raw samples as they arrive (audio thread), for live transcription

    WaveInEvent? _in;
    readonly MemoryStream _pcm = new();
    readonly object _gate = new();
    public bool IsRecording { get; private set; }
    public float PeakLevel { get; private set; }

    public static IReadOnlyList<string> Devices()
    {
        var list = new List<string>();
        for (int i = 0; i < WaveInEvent.DeviceCount; i++) list.Add(WaveInEvent.GetCapabilities(i).ProductName);
        return list;
    }

    public static int ResolveDevice(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return -1; // WAVE_MAPPER = Windows default input
        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
            if (WaveInEvent.GetCapabilities(i).ProductName.Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    public void Start(string deviceName)
    {
        if (IsRecording) return;
        lock (_gate) { _pcm.SetLength(0); PeakLevel = 0; }
        _in = new WaveInEvent
        {
            DeviceNumber = ResolveDevice(deviceName),
            WaveFormat = new WaveFormat(SampleRate, 16, 1),
            BufferMilliseconds = 40
        };
        _in.DataAvailable += OnData;
        _in.StartRecording();
        IsRecording = true;
    }

    void OnData(object? s, WaveInEventArgs e)
    {
        lock (_gate) _pcm.Write(e.Buffer, 0, e.BytesRecorded);
        double sum = 0; int n = e.BytesRecorded / 2;
        var chunk = new float[n];
        for (int i = 0; i < n; i++) { short v = BitConverter.ToInt16(e.Buffer, i * 2); sum += (double)v * v; chunk[i] = v / 32768f; }
        float rms = n == 0 ? 0 : (float)(Math.Sqrt(sum / n) / 32768.0);
        Chunk?.Invoke(chunk);
        if (rms > PeakLevel) PeakLevel = rms;
        Level?.Invoke(rms);
    }

    /// <summary>Stops and returns the audio as float samples in [-1,1].</summary>
    public float[] Stop()
    {
        if (_in != null)
        {
            try { _in.StopRecording(); } catch { }
            _in.DataAvailable -= OnData;
            _in.Dispose();
            _in = null;
        }
        IsRecording = false;
        lock (_gate)
        {
            var bytes = _pcm.ToArray();
            var samples = new float[bytes.Length / 2];
            for (int i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
            return samples;
        }
    }

    public void Cancel() { Stop(); }

    public static void SaveWav(string path, float[] samples)
    {
        using var w = new WaveFileWriter(path, new WaveFormat(SampleRate, 16, 1));
        var buf = new short[samples.Length];
        for (int i = 0; i < buf.Length; i++) buf[i] = (short)Math.Clamp(samples[i] * 32767f, -32768, 32767);
        var bytes = new byte[buf.Length * 2];
        Buffer.BlockCopy(buf, 0, bytes, 0, bytes.Length);
        w.Write(bytes, 0, bytes.Length);
    }

    public void Dispose() => Stop();
}
