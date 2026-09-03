using LibreHardwareMonitor.Hardware;

namespace FullRGB.Sensors;

/// <summary>Reads CPU/GPU temperatures via LibreHardwareMonitor kernel driver (needs admin).</summary>
public sealed class TemperatureProvider : IDisposable
{
    private Computer? _computer;
    private readonly object _lock = new();
    private bool _disposed;

    public void Start()
    {
        lock (_lock)
        {
            if (_computer is not null || _disposed) return;
            var c = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = false,
                IsMotherboardEnabled = false,
                IsStorageEnabled = false,
                IsNetworkEnabled = false,
                IsControllerEnabled = false,
                IsPsuEnabled = false,
                IsBatteryEnabled = false,
            };
            c.Open();
            _computer = c;
        }
    }

    public (double? cpu, double? gpu) Read()
    {
        // Update() is not thread-safe and can block for tens of ms; serialise and never
        // let a transient sensor failure escape into the render loop.
        lock (_lock)
        {
            if (_computer is null || _disposed) return (null, null);
            double? cpu = null, gpu = null;
            try
            {
                foreach (var hw in _computer.Hardware)
                {
                    hw.Update();
                    if (hw.HardwareType == HardwareType.Cpu && cpu is null)
                        cpu = hw.Sensors.Where(s => s.SensorType == SensorType.Temperature)
                                        .Select(s => s.Value).Where(v => v.HasValue)
                                        .OrderByDescending(v => v).FirstOrDefault();
                    if ((hw.HardwareType == HardwareType.GpuNvidia || hw.HardwareType == HardwareType.GpuIntel ||
                         hw.HardwareType == HardwareType.GpuAmd) && gpu is null)
                        gpu = hw.Sensors.Where(s => s.SensorType == SensorType.Temperature &&
                                    (s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) ||
                                     s.Name.Contains("GPU", StringComparison.OrdinalIgnoreCase)))
                                        .Select(s => s.Value).Where(v => v.HasValue)
                                        .OrderByDescending(v => v).FirstOrDefault();
                }
            }
            catch { /* sensors can fail transiently */ }
            return (cpu, gpu);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            try { _computer?.Close(); } catch { }
            _computer = null;
        }
    }
}

/// <summary>
/// System-audio analyser via WASAPI loopback (no mic permission needed).
/// Bands come from a real FFT: the previous version split the buffer by SAMPLE POSITION,
/// which is time, not frequency, so "bass" and "treble" were the same signal delayed.
/// </summary>
public sealed class AudioProvider : IDisposable
{
    private const int FftSize = 1024;               // ~23 ms at 44.1 kHz, 43 Hz per bin

    private NAudio.Wave.WasapiLoopbackCapture? _capture;
    private readonly object _lock = new();
    private double _level, _bass, _mid, _treble, _beat;

    private readonly double[] _mono = new double[FftSize];
    private readonly double[] _re = new double[FftSize];
    private readonly double[] _im = new double[FftSize];
    private readonly double[] _window = new double[FftSize];
    private int _monoFill;
    private int _sampleRate = 48000;

    public double Level { get { lock (_lock) return _level; } }
    public double Bass { get { lock (_lock) return _bass; } }
    public double Mid { get { lock (_lock) return _mid; } }
    public double Treble { get { lock (_lock) return _treble; } }
    /// <summary>Kick-drum onset envelope 0..1: jumps to 1 on a bass hit, then falls fast.</summary>
    public double Beat { get { lock (_lock) return _beat; } }

    /// <summary>Null until capture starts; used by the UI to explain a silent music effect.</summary>
    public string? LastError { get; private set; }

    public AudioProvider()
    {
        for (int i = 0; i < FftSize; i++) // Hann window
            _window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1));
    }

    public void Start()
    {
        if (_capture is not null) return;
        try
        {
            _capture = new NAudio.Wave.WasapiLoopbackCapture();
            _sampleRate = _capture.WaveFormat.SampleRate;
            _capture.DataAvailable += OnData;
            _capture.StartRecording();
        }
        catch (Exception e)
        {
            LastError = e.Message;
            _capture = null;
            throw;
        }
    }

    /// <summary>Bytes consumed by one sample frame for a given mix format.</summary>
    public static int GetSampleStride(NAudio.Wave.WaveFormat fmt)
        => fmt.Channels * (fmt.BitsPerSample / 8);

    private void OnData(object? sender, NAudio.Wave.WaveInEventArgs e)
    {
        var fmt = _capture?.WaveFormat;
        if (fmt is null) return;
        int bytesPerSample = fmt.BitsPerSample / 8;
        int stride = GetSampleStride(fmt);
        if (bytesPerSample <= 0 || stride <= 0) return;
        int sampleCount = e.BytesRecorded / stride;
        if (sampleCount <= 0) return;

        bool isFloat = fmt.Encoding == NAudio.Wave.WaveFormatEncoding.IeeeFloat
                    || (fmt.Encoding == NAudio.Wave.WaveFormatEncoding.Extensible && bytesPerSample == 4);

        double sumSq = 0;
        for (int i = 0; i < sampleCount; i++)
        {
            double sum = 0;
            for (int ch = 0; ch < fmt.Channels; ch++)
            {
                int off = i * stride + ch * bytesPerSample;
                if (off + bytesPerSample > e.BytesRecorded) break;
                sum += ReadSample(e.Buffer, off, bytesPerSample, isFloat);
            }
            double v = fmt.Channels > 0 ? sum / fmt.Channels : 0;
            sumSq += v * v;

            // fill the FFT ring; analyse whenever it is full
            _mono[_monoFill++] = v;
            if (_monoFill == FftSize)
            {
                Analyse();
                _monoFill = 0;
            }
        }

        double rms = Math.Sqrt(sumSq / sampleCount);
        // Fast attack + slow release: beats must punch instantly, then fall smoothly.
        // The old symmetric blend smeared kick drums into a blurry glow that felt off-beat.
        double target = Math.Clamp(rms * 4.0, 0, 1);
        lock (_lock)
            _level = target > _level
                ? _level * 0.5 + target * 0.5
                : _level * 0.85 + target * 0.15;
    }

    private static double ReadSample(byte[] buf, int off, int bytesPerSample, bool isFloat) => bytesPerSample switch
    {
        4 => isFloat ? BitConverter.ToSingle(buf, off) : BitConverter.ToInt32(buf, off) / 2147483648.0,
        3 => ((buf[off + 2] << 16 | buf[off + 1] << 8 | buf[off]) << 8 >> 8) / 8388608.0,
        2 => BitConverter.ToInt16(buf, off) / 32768.0,
        1 => (buf[off] - 128) / 128.0,
        _ => 0,
    };

    /// <summary>Windowed FFT of the current block, folded into three frequency bands.</summary>
    private void Analyse()
    {
        for (int i = 0; i < FftSize; i++)
        {
            _re[i] = _mono[i] * _window[i];
            _im[i] = 0;
        }
        Fft(_re, _im);

        double binHz = (double)_sampleRate / FftSize;
        double bass = 0, mid = 0, treble = 0;
        int nB = 0, nM = 0, nT = 0;
        for (int k = 1; k < FftSize / 2; k++)
        {
            double hz = k * binHz;
            double mag = Math.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]) * 2.0 / FftSize;
            if (hz < 250) { bass += mag; nB++; }
            else if (hz < 4000) { mid += mag; nM++; }
            else if (hz < 16000) { treble += mag; nT++; }
        }
        // per-band gains: treble energy is naturally far lower than bass in music
        double B(double v, int n, double gain) => n == 0 ? 0 : Math.Clamp(v / n * gain, 0, 1);
        double nb = B(bass, nB, 90), nm = B(mid, nM, 220), nt = B(treble, nT, 420);
        lock (_lock)
        {
            // same fast-attack / slow-release envelope as the overall level
            _bass = nb > _bass ? _bass * 0.5 + nb * 0.5 : _bass * 0.85 + nb * 0.15;
            _mid = nm > _mid ? _mid * 0.5 + nm * 0.5 : _mid * 0.85 + nm * 0.15;
            _treble = nt > _treble ? _treble * 0.5 + nt * 0.5 : _treble * 0.85 + nt * 0.15;
            // kick onset: instantaneous bass far above its smoothed self = a hit
            if (nb > 0.25 && nb > _bass * 1.35 + 0.08) _beat = 1.0;
            else _beat *= 0.78; // ~90 ms falloff at the ~21 ms analysis rate
        }
    }

    /// <summary>In-place iterative radix-2 FFT (length must be a power of two).</summary>
    internal static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        if (n < 2 || (n & (n - 1)) != 0) throw new ArgumentException("FFT length must be a power of two");

        // bit-reversal permutation
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double curR = 1, curI = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = i + k + len / 2;
                    double xr = re[b] * curR - im[b] * curI;
                    double xi = re[b] * curI + im[b] * curR;
                    re[b] = re[a] - xr; im[b] = im[a] - xi;
                    re[a] += xr; im[a] += xi;
                    double nr = curR * wr - curI * wi;
                    curI = curR * wi + curI * wr;
                    curR = nr;
                }
            }
        }
    }

    public void Dispose()
    {
        try
        {
            if (_capture is not null)
            {
                _capture.DataAvailable -= OnData;
                _capture.StopRecording();
                _capture.Dispose();
            }
        }
        catch { }
        _capture = null;
    }
}
