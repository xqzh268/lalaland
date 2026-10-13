using System.Runtime.InteropServices;

namespace LaLaLand.Services;

/// <param name="Band0">Bass … <param name="Band3">treble, each normalised to 0–1 (adaptive gain).</param></param>
/// <param name="Bass">Low-frequency envelope 0–1 (kick drum / bass), used for "breathing".</param>
public readonly record struct AudioFrame(float Band0, float Band1, float Band2, float Band3, float Bass, float Level);

/// <summary>
/// Real-time music analysis from WASAPI loopback capture.
/// <para>
/// Prefers <b>process loopback</b> (Windows 10 2004+): only the player's process tree is captured, so game audio
/// cannot pollute beat detection. Falls back to loopback of the default playback device.
/// </para>
/// A 1024-point FFT runs every ~400 samples; results are raised on the capture thread (~60 frames/s).
/// The analyzer only runs while someone needs it — callers start/stop it explicitly.
/// </summary>
public sealed class AudioAnalyzer : IDisposable
{
    private const int SampleRate = 48000;
    private const int FftSize = 1024;
    private const int Hop = 400;              // 120 analyses per second
    private const int FramesPerPublish = 6;   // ≈ 20 published frames per second (plenty for 4 small bars)

    public event Action<AudioFrame>? Frame;
    public event Action<float>? Beat;

    private Thread? _thread;
    private ManualResetEvent? _stop;
    private uint _targetPid;

    public bool IsRunning => _thread is not null;

    /// <summary>Capture mode actually in use ("process" / "device"), for diagnostics.</summary>
    public string? Mode { get; private set; }

    public uint TargetProcessId => _targetPid;

    /// <param name="processId">Player process to capture (its whole process tree); 0 = default playback device.</param>
    public void Start(uint processId)
    {
        if (_thread is not null && processId == _targetPid)
            return;
        Stop();
        _targetPid = processId;
        _stop = new ManualResetEvent(false);
        var stop = _stop;
        _thread = new Thread(() => CaptureLoop(processId, stop)) { IsBackground = true, Name = "LaLaLand audio" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public void Stop()
    {
        if (_thread is null)
            return;
        _stop!.Set();
        _thread.Join(1000);
        _thread = null;
        _stop.Dispose();
        _stop = null;
        Mode = null;
    }

    public void Dispose() => Stop();

    // ---------------------------------------------------------------- capture

    private void CaptureLoop(uint pid, ManualResetEvent stop)
    {
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        using var dataReady = new AutoResetEvent(false);
        IntPtr format = IntPtr.Zero;
        try
        {
            client = pid != 0 ? ActivateProcessLoopback(pid) : null;
            Mode = client is not null ? "process" : "device";
            client ??= ActivateDeviceLoopback();
            if (client is null)
            {
                Log.Info("Audio capture unavailable");
                return;
            }

            // 16-bit stereo PCM at 48 kHz; AUTOCONVERTPCM lets the engine resample whatever the source is.
            format = Marshal.AllocHGlobal(20);
            Marshal.WriteInt16(format, 0, 1);                 // WAVE_FORMAT_PCM
            Marshal.WriteInt16(format, 2, 2);                 // channels
            Marshal.WriteInt32(format, 4, SampleRate);
            Marshal.WriteInt32(format, 8, SampleRate * 4);    // avg bytes/s
            Marshal.WriteInt16(format, 12, 4);                // block align
            Marshal.WriteInt16(format, 14, 16);               // bits
            Marshal.WriteInt16(format, 16, 0);                // cbSize

            const uint flags = 0x00020000 /* LOOPBACK */ | 0x00040000 /* EVENTCALLBACK */ |
                               0x80000000 /* AUTOCONVERTPCM */ | 0x08000000 /* SRC_DEFAULT_QUALITY */;
            Marshal.ThrowExceptionForHR(client.Initialize(0, flags, 200_000, 0, format, IntPtr.Zero));
            Marshal.ThrowExceptionForHR(client.SetEventHandle(dataReady.SafeWaitHandle.DangerousGetHandle()));
            var iid = typeof(IAudioCaptureClient).GUID;
            Marshal.ThrowExceptionForHR(client.GetService(ref iid, out var captureObj));
            capture = (IAudioCaptureClient)captureObj;
            Marshal.ThrowExceptionForHR(client.Start());
            Log.Info($"Audio capture started ({Mode}{(Mode == "process" ? $", pid {pid}" : "")})");

            var analysis = new Analysis();
            var handles = new WaitHandle[] { stop, dataReady };
            while (true)
            {
                int signalled = WaitHandle.WaitAny(handles, 250);
                if (signalled == 0)
                    break;

                while (capture.GetNextPacketSize(out uint packet) == 0 && packet > 0)
                {
                    if (capture.GetBuffer(out var data, out uint frames, out uint bufferFlags, out _, out _) != 0)
                        break;
                    bool silent = (bufferFlags & 0x2) != 0;
                    analysis.Push(data, (int)frames, silent, this);
                    capture.ReleaseBuffer(frames);
                }
            }
            client.Stop();
        }
        catch (Exception ex)
        {
            Log.Error("Audio capture failed", ex);
            Mode = null;
        }
        finally
        {
            if (capture is not null)
                Marshal.ReleaseComObject(capture);
            if (client is not null)
                Marshal.ReleaseComObject(client);
            if (format != IntPtr.Zero)
                Marshal.FreeHGlobal(format);
        }
    }

    /// <summary>Process loopback via ActivateAudioInterfaceAsync("VAD\Process_Loopback").</summary>
    private static IAudioClient? ActivateProcessLoopback(uint pid)
    {
        IntPtr activation = IntPtr.Zero, propVariant = IntPtr.Zero;
        try
        {
            activation = Marshal.AllocHGlobal(12);
            Marshal.WriteInt32(activation, 0, 1);              // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
            Marshal.WriteInt32(activation, 4, (int)pid);       // TargetProcessId
            Marshal.WriteInt32(activation, 8, 0);              // PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE

            propVariant = Marshal.AllocHGlobal(24);
            for (int i = 0; i < 24; i += 4)
                Marshal.WriteInt32(propVariant, i, 0);
            Marshal.WriteInt16(propVariant, 0, 65);            // VT_BLOB
            Marshal.WriteInt32(propVariant, 8, 12);            // blob size
            Marshal.WriteIntPtr(propVariant, 16, activation);  // blob data

            var handler = new ActivationHandler();
            int hr = ActivateAudioInterfaceAsync(@"VAD\Process_Loopback", typeof(IAudioClient).GUID, propVariant, handler, out var operation);
            if (hr != 0 || !handler.Done.Wait(3000))
                return null;
            GC.KeepAlive(operation);
            return handler.Result == 0 ? handler.Client : null;
        }
        catch (Exception ex)
        {
            Log.Error("Process loopback unavailable", ex);
            return null;
        }
        finally
        {
            if (propVariant != IntPtr.Zero) Marshal.FreeHGlobal(propVariant);
            if (activation != IntPtr.Zero) Marshal.FreeHGlobal(activation);
        }
    }

    private static IAudioClient? ActivateDeviceLoopback()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        if (enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out var device) != 0)
            return null;
        var iid = typeof(IAudioClient).GUID;
        return device.Activate(ref iid, 0x17, IntPtr.Zero, out var client) == 0 ? (IAudioClient)client : null;
    }

    // ---------------------------------------------------------------- analysis

    private sealed class Analysis
    {
        private static readonly (int Lo, int Hi)[] Bands =
        [
            (Bin(50), Bin(200)), (Bin(200), Bin(800)), (Bin(800), Bin(3000)), (Bin(3000), Bin(10000)),
        ];
        private static readonly (int Lo, int Hi) BassBand = (Bin(40), Bin(150));
        private static readonly float[] Window = Enumerable.Range(0, FftSize)
            .Select(i => (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1)))).ToArray();

        private readonly float[] _ring = new float[FftSize];
        private readonly float[] _re = new float[FftSize];
        private readonly float[] _im = new float[FftSize];
        private int _write;
        private int _sinceHop;
        private int _sincePublish;

        // Adaptive normalisation and smoothing
        private readonly float[] _peakDb = [-30, -30, -30, -30];
        private readonly float[] _smoothed = new float[4];
        private float _bassPeakDb = -30, _bass, _levelPeakDb = -30, _level;

        // Beat detection on bass power
        private readonly float[] _history = new float[120];
        private int _historyIndex;
        private float _lastBass;
        private long _lastBeatTicks;

        private static int Bin(double hz) => (int)Math.Round(hz * FftSize / SampleRate);

        public void Push(IntPtr data, int frames, bool silent, AudioAnalyzer owner)
        {
            for (int i = 0; i < frames; i++)
            {
                float mono = 0;
                if (!silent)
                {
                    short l = Marshal.ReadInt16(data, i * 4);
                    short r = Marshal.ReadInt16(data, i * 4 + 2);
                    mono = (l + r) / 65536f;
                }
                _ring[_write] = mono;
                _write = (_write + 1) % FftSize;
                if (++_sinceHop >= Hop)
                {
                    _sinceHop = 0;
                    Analyze(owner);
                }
            }
        }

        private void Analyze(AudioAnalyzer owner)
        {
            for (int i = 0; i < FftSize; i++)
            {
                _re[i] = _ring[(_write + i) % FftSize] * Window[i];
                _im[i] = 0;
            }
            Fft(_re, _im);

            const float dt = (float)Hop / SampleRate;
            for (int b = 0; b < 4; b++)
            {
                float db = BandDb(Bands[b]);
                _smoothed[b] = Smooth(_smoothed[b], Normalize(db, ref _peakDb[b], dt), 0.55f, 0.12f);
            }

            float bassDb = BandDb(BassBand);
            _bass = Smooth(_bass, Normalize(bassDb, ref _bassPeakDb, dt), 0.5f, 0.1f);
            float levelDb = BandDb((Bin(40), Bin(12000)));
            _level = Smooth(_level, Normalize(levelDb, ref _levelPeakDb, dt), 0.4f, 0.08f);

            DetectBeat(BandPower(BassBand), owner);

            if (++_sincePublish >= FramesPerPublish)
            {
                _sincePublish = 0;
                owner.Frame?.Invoke(new AudioFrame(_smoothed[0], _smoothed[1], _smoothed[2], _smoothed[3], _bass, _level));
            }
        }

        private void DetectBeat(float power, AudioAnalyzer owner)
        {
            // Compare the current bass power with the recent average: a sudden jump is a kick / beat.
            float average = 0;
            foreach (var h in _history)
                average += h;
            average /= _history.Length;

            _history[_historyIndex] = power;
            _historyIndex = (_historyIndex + 1) % _history.Length;

            long now = Environment.TickCount64;
            bool rising = power > _lastBass;
            _lastBass = power;
            if (average <= 1e-7f || !rising || now - _lastBeatTicks < 280)
                return;

            float ratio = power / average;
            if (ratio > 1.6f)
            {
                _lastBeatTicks = now;
                owner.Beat?.Invoke(Math.Clamp((ratio - 1.6f) / 1.6f + 0.35f, 0.3f, 1f));
            }
        }

        private float BandPower((int Lo, int Hi) band)
        {
            float sum = 0;
            int lo = Math.Max(1, band.Lo), hi = Math.Max(lo, band.Hi);
            for (int k = lo; k <= hi; k++)
                sum += _re[k] * _re[k] + _im[k] * _im[k];
            return sum / (hi - lo + 1);
        }

        private float BandDb((int Lo, int Hi) band) => 10f * MathF.Log10(BandPower(band) + 1e-12f);

        /// <summary>Maps dB into 0–1 against a slowly decaying peak (40 dB range), so quiet and loud songs both move.</summary>
        private static float Normalize(float db, ref float peakDb, float dt)
        {
            peakDb = Math.Max(db, peakDb - 4f * dt);   // peak falls 4 dB/s
            if (peakDb < -60f) peakDb = -60f;
            float floor = peakDb - 40f;
            if (db < -90f)
                return 0;
            float v = Math.Clamp((db - floor) / (peakDb - floor), 0f, 1f);
            return v * v;                               // emphasise peaks
        }

        private static float Smooth(float current, float target, float attack, float release) =>
            current + (target - current) * (target > current ? attack : release);

        /// <summary>In-place iterative radix-2 FFT.</summary>
        private static void Fft(float[] re, float[] im)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1)
                    j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    (re[i], re[j]) = (re[j], re[i]);
                    (im[i], im[j]) = (im[j], im[i]);
                }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double angle = -2 * Math.PI / len;
                float wr = (float)Math.Cos(angle), wi = (float)Math.Sin(angle);
                for (int i = 0; i < n; i += len)
                {
                    float cr = 1, ci = 0;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int a = i + k, b = a + len / 2;
                        float tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - tr; im[b] = im[a] - ti;
                        re[a] += tr; im[a] += ti;
                        float ncr = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr;
                        cr = ncr;
                    }
                }
            }
        }
    }

    // ---------------------------------------------------------------- COM interop

    [DllImport("Mmdevapi.dll", ExactSpelling = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation operation);

    [ComVisible(true)]
    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Done = new(false);
        public IAudioClient? Client;
        public int Result = -1;

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            try
            {
                operation.GetActivateResult(out Result, out var instance);
                Client = instance as IAudioClient;
            }
            catch
            {
                Result = -1;
            }
            Done.Set();
            return 0;
        }
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject;

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }
}
