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

    // Ambient screen colour: the WASAPI callback thread also decimates the screen every
    // ~80 ms (12 fps is plenty for ambient light; a full 30 fps copy is pointless).
    // Three horizontal bands (top/middle/bottom), each an EMA-smoothed 0..1 RGB triple.
    private readonly double[][] _rows = new[]
    {
        new double[3], new double[3], new double[3],
    };
    private int _screenSamples;             // 0 = never sampled
    private long _lastScreenPoll;           // Environment.TickCount64 ms of the last grab
    private const int ScreenPollMs = 80;    // ~12 fps screen sampling
    private const int ScreenRows = 3;
    private System.Threading.Timer? _screenTimer;

    private readonly double[] _mono = new double[FftSize];
    private readonly double[] _re = new double[FftSize];
    private readonly double[] _im = new double[FftSize];
    private readonly double[] _window = new double[FftSize];
    private int _monoFill;
    private int _sampleRate = 48000;

    /// <summary>Slow per-band baseline (bass/mid/treble): the track's own level for each band. The
    /// visible value is measured AGAINST this, which is what makes it express dynamics.</summary>
    private readonly double[] _bandBase = new double[3];

    /// <summary>Raw (absolute) mean magnitude of each band this frame — drives band SELECTION.</summary>
    private readonly double[] _bandRaw = new double[3];

    /// <summary>Baseline adaptation. Rising fast (~2.7 s) so a quiet intro does not leave the value
    /// pinned at 1.0 once the music gets loud, falling slowly (~15 s) so a brief quiet bar does not
    /// re-scale everything.</summary>
    private const double BaselineRiseAlpha = 0.008;
    private const double BaselineFallAlpha = 0.0014;

    /// <summary>Visible window around the baseline, in dB.</summary>
    private const double BaselineFloorDb = -12, BaselineCeilDb = 9;

    // ---- automatic band selection ----
    // A fixed band cannot work for every track: "bass" does nothing on an acoustic song, "treble"
    // does nothing on hip-hop, so the user had to re-pick per song. Instead the provider scores how
    // RHYTHMIC each band currently is and follows whichever one is actually carrying the beat.
    private readonly double[] _autoMean = { 0.05, 0.05, 0.05 };   // ~1 s mean of each band
    private readonly double[] _autoMove = { 0.02, 0.02, 0.02 };   // ~1 s mean of |frame-to-frame delta|
    private readonly double[] _autoPrev = new double[3];          // previous raw band values
    private int _autoBand;                                        // 0 = bass, 1 = mid, 2 = treble
    private double _autoLevel;

    /// <summary>Level of the band the detector currently considers rhythmic — what "auto" renders.</summary>
    public double AutoLevel { get { lock (_lock) return _autoLevel; } }

    /// <summary>Which band auto picked (0 bass, 1 mid, 2 treble). Shown in the UI so the choice is
    /// visible instead of being a black box.</summary>
    public int AutoBandIndex { get { lock (_lock) return _autoBand; } }

    /// <summary>
    /// Rhythm score of one band: how much it MOVES relative to its own average.
    ///
    /// Dividing by the mean is the whole point — a loud bass line barely moves relative to itself,
    /// while a quiet hi-hat moves a lot, so the score tracks the beat rather than the volume. The
    /// floor excludes a band that simply has no content in this track (near-silent noise would
    /// otherwise score enormously on the tiniest wobble).
    /// </summary>
    internal static double RhythmScore(double mean, double move)
        => mean < 0.04 ? 0 : move / Math.Max(0.02, mean);

    /// <summary>
    /// Which band should the lights follow? Pure, so the rule is unit-tested rather than only
    /// exercised by ear.
    ///
    /// <paramref name="incumbent"/> gets hysteresis: it keeps the crown unless a challenger is
    /// CLEARLY better (25 %), otherwise two similar bands would trade places mid-song and the
    /// lighting would appear to change personality for no reason.
    /// </summary>
    internal static int PickBand(double[] score, int incumbent)
    {
        if (score.Length < 3) return Math.Clamp(incumbent, 0, 2);
        int best = 0;
        for (int i = 1; i < 3; i++) if (score[i] > score[best]) best = i;
        if (best != incumbent && score[best] < score[Math.Clamp(incumbent, 0, 2)] * 1.25)
            best = Math.Clamp(incumbent, 0, 2);
        return best;
    }

    public static string BandName(int index) => index switch { 1 => "mid", 2 => "treble", _ => "bass" };

    /// <summary>
    /// Linear amplitude to 0..1 over a dB window. A LINEAR scale cannot work here: normal music
    /// sits around RMS 0.05-0.30, so the old "rms * 4.0" reached full scale at RMS 0.25 and every
    /// louder passage looked identical — the "volume does not follow the song" report.
    /// </summary>
    internal static double ToUnit(double amplitude, double floorDb, double ceilingDb)
    {
        if (amplitude <= 0 || double.IsNaN(amplitude)) return 0;
        double db = 20 * Math.Log10(amplitude);
        if (double.IsNaN(db) || double.IsInfinity(db)) return 0;
        return Math.Clamp((db - floorDb) / (ceilingDb - floorDb), 0, 1);
    }

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
        // Screen sampling on its OWN timer thread — never inside the WASAPI callback, where a
        // 2–4 ms GDI grab would delay audio analysis (and a silent PC fires no callbacks at
        // all, so ambient screen colour would freeze exactly when nothing plays).
        if (_screenTimer is null)
            _screenTimer = new System.Threading.Timer(_ => PollScreenColour(), null, 0, ScreenPollMs);
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
        //
        // Release speed matters as much as attack: 0.15/frame at ~43 analysis frames/s took
        // ~600 ms to reach the 2 % noise gate after music stopped — the lights kept glowing
        // 1–2 s after silence. 0.45 falls below the gate in ~5 frames (~120 ms) while still
        // smoothing the perceptible decay between beats (target 0 = instant would flicker).
        //
        // The target is dB-mapped, not linear: -55 dB is silence-ish and -5 dB is a loud master,
        // so quiet and loud passages actually look different.
        double target = ToUnit(rms, -55, -5);
        lock (_lock)
            _level = target > _level
                ? _level * 0.5 + target * 0.5
                : _level * 0.55 + target * 0.45;
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
        // Per-band AGC instead of a fixed gain. The old "mean magnitude * 90/220/420" pinned every
        // band at 1.0 for ordinary music (a kick drum alone drove bass well past the clamp), so the
        // bars stopped moving and looked out of sync with the song. Normalising against a
        // slowly-decaying peak keeps ~24 dB of visible range whatever the track's level is.
        double ampB = RawBand(bass, nB), ampM = RawBand(mid, nM), ampT = RawBand(treble, nT);
        double nb = Relative(0, ampB), nm = Relative(1, ampM), nt = Relative(2, ampT);
        lock (_lock)
        {
            // Capture the PREVIOUS smoothed values: the onset test below must compare the
            // instantaneous band against what the envelope looked like BEFORE this frame. Reading
            // them after the update (the old code) compared nb against a value that already
            // contained half of nb, so only extreme jumps ever registered and ordinary kick drums
            // were silently missed — the "bass does not follow the song" report.
            double prevBass = _bass, prevMid = _mid, prevTreble = _treble;
            // Same fast-attack envelope as the overall level; release 0.45 (not 0.15) so the
            // bands hit the 2 % noise gate in ~120 ms after the music stops — the slow 0.15
            // kept the bass bar glowing ~1.5 s into silence, the exact "lights lag the sound"
            // complaint the overall-level fix answers. Falloff between beats stays smooth.
            _bass = Envelope(_bass, nb);
            _mid = Envelope(_mid, nm);
            _treble = Envelope(_treble, nt);

            // ---- which band is carrying the rhythm right now? ----
            // Selection runs on the RAW amplitudes, not the baseline-relative values: the "this band
            // has no content in this track" floor only means something on an absolute scale.
            double[] rawAmp = { ampB, ampM, ampT };
            for (int i = 0; i < 3; i++)
            {
                // Slow means (~1 s at the ~47 Hz analysis rate) so this follows the TRACK, not the
                // beat: a single drum hit must not flip the choice.
                _autoMean[i] += (rawAmp[i] - _autoMean[i]) * 0.02;
                _autoMove[i] += (Math.Abs(rawAmp[i] - _autoPrev[i]) - _autoMove[i]) * 0.02;
                _autoPrev[i] = rawAmp[i];
                _bandRaw[i] = rawAmp[i];
            }
            double[] score =
            {
                RhythmScore(_autoMean[0], _autoMove[0]),
                RhythmScore(_autoMean[1], _autoMove[1]),
                RhythmScore(_autoMean[2], _autoMove[2]),
            };
            int best = PickBand(score, _autoBand);
            _autoBand = best;
            // Unsmoothed on purpose: the punch of the beat lives in the instantaneous value.
            double[] rel = { nb, nm, nt };
            _autoLevel = rel[best];

            // Onset detection follows the AUTO band. Watching only the bass meant a track with no
            // kick (acoustic, classical, most vocals) never produced a beat at all.
            double inst = rel[best];
            double prevInst = _autoBand == 0 ? prevBass : _autoBand == 1 ? prevMid : prevTreble;
            // kick onset: instantaneous band far above its smoothed self = a hit
            if (inst > 0.25 && inst > prevInst * 1.35 + 0.08) _beat = 1.0;
            else _beat *= 0.78; // ~90 ms falloff at the ~21 ms analysis rate
        }
    }

    private static double Envelope(double current, double target)
        => target > current ? current * 0.5 + target * 0.5 : current * 0.55 + target * 0.45;

    /// <summary>Mean magnitude of one band, before any normalisation.</summary>
    internal static double RawBand(double sum, int count) => count == 0 ? 0 : sum / count;

    /// <summary>
    /// Pure core of <see cref="Relative"/>: how far a band sits above its own baseline, over a
    /// -12..+9 dB window. At the baseline this is ~0.57, not 1.0 — see the class remarks.
    /// </summary>
    internal static double RelativeToBaseline(double amp, double baseLine)
        => amp <= 0 || baseLine <= 0 ? 0 : ToUnit(amp / baseLine, BaselineFloorDb, BaselineCeilDb);

    /// <summary>
    /// A band's visible value: how far ABOVE its own slow baseline it is right now.
    ///
    /// This used to normalise against a decaying PEAK, which pinned sustained music at 1.0: a chorus
    /// held the strip at full brightness, a 6 dB drop only reached 0.75, and every quiet/loud
    /// contrast disappeared — the "lights just sit there for long stretches" report. A baseline is
    /// the track's own level, so measuring against it makes the value express the music's DYNAMICS,
    /// which is the entire point of a visualiser. A hit pushes it up, a drop pulls it down.
    ///
    /// Called only from the WASAPI callback thread, so the baseline needs no lock.
    /// </summary>
    private double Relative(int index, double amp)
    {
        double baseLine = _bandBase[index];
        _bandBase[index] = baseLine <= 0
            ? amp
            : baseLine + (amp - baseLine) * (amp > baseLine ? BaselineRiseAlpha : BaselineFallAlpha);
        return RelativeToBaseline(amp, baseLine);
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
            if (_screenTimer is not null)
            {
                _screenTimer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
                _screenTimer.Dispose();
            }
        }
        catch { }
        _screenTimer = null;
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

    // ---------- ambient screen colour (Gaming + Ambient effects) ----------

    /// <summary>
    /// Grabs the screen, averages it into three horizontal bands and smooths the result.
    /// Called on the WASAPI callback thread (which already runs whenever audio devices
    /// produce data) but rate-limited to <see cref="ScreenPollMs"/>.
    /// GDI CopyFromScreen needs no elevation; one grab per 80 ms is negligible.
    /// </summary>
    private void PollScreenColour()
    {
        try
        {
            long now = Environment.TickCount64;
            if (now - _lastScreenPoll < ScreenPollMs) return;
            _lastScreenPoll = now;

            var bounds = System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? System.Drawing.Rectangle.Empty;
            if (bounds.Width <= 0 || bounds.Height <= 0) return;

            int rowH = Math.Max(1, bounds.Height / ScreenRows);
            using var full = new System.Drawing.Bitmap(bounds.Width, bounds.Height);
            using (var g = System.Drawing.Graphics.FromImage(full))
                g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size);

            double a = 1.0 / Math.Min(_screenSamples + 1, 8);  // converge ~0.6 s, then track at 1/8
            for (int rI = 0; rI < ScreenRows; rI++)
            {
                // one downscaled strip per band: DrawImage averages the whole band into 1×N
                using var strip = new System.Drawing.Bitmap(1, 8);
                using (var g = System.Drawing.Graphics.FromImage(strip))
                    g.DrawImage(full, new System.Drawing.Rectangle(0, 0, 1, 8),
                                new System.Drawing.Rectangle(0, rI * rowH, bounds.Width, rowH),
                                System.Drawing.GraphicsUnit.Pixel);
                double rr = 0, gg = 0, bb = 0;
                for (int y = 0; y < 8; y++)
                {
                    var c = strip.GetPixel(0, y);
                    rr += c.R; gg += c.G; bb += c.B;
                }
                rr /= 2040.0; gg /= 2040.0; bb /= 2040.0;   // 8 × 255
                _rows[rI][0] += (rr - _rows[rI][0]) * a;
                _rows[rI][1] += (gg - _rows[rI][1]) * a;
                _rows[rI][2] += (bb - _rows[rI][2]) * a;
            }
            _screenSamples++;
        }
        catch { /* screen capture is best-effort (locked desktop, RDP, headless) */ }
    }

    /// <summary>Probe hook: how many screen samples have landed (0 = capture not working).</summary>
    public int ScreenSamples => _screenSamples;

    /// <summary>
    /// Copies the smoothed band colours into the context. Returns false until at least one
    /// screen grab has completed (the renderer then falls back to the primary colour).
    /// Lock-free: the writer only ever assigns whole doubles and the renderer tolerates a
    /// torn mix of two consecutive samples (both are valid smoothed colours).
    /// </summary>
    public bool FillScreenContext(Effects.EffectContext ctx)
    {
        if (_screenSamples == 0) return false;
        for (int rI = 0; rI < ScreenRows; rI++)
        {
            double r = _rows[rI][0], g = _rows[rI][1], b = _rows[rI][2];
            switch (rI)
            {
                case 0: ctx.ScreenRow0R = r; ctx.ScreenRow0G = g; ctx.ScreenRow0B = b; break;
                case 1: ctx.ScreenRow1R = r; ctx.ScreenRow1G = g; ctx.ScreenRow1B = b; break;
                case 2: ctx.ScreenRow2R = r; ctx.ScreenRow2G = g; ctx.ScreenRow2B = b; break;
            }
        }
        return true;
    }
}
