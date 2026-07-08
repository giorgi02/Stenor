using System.IO;
using NAudio.CoreAudioApi;
using Stenor.Interfaces;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Utils;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Stenor.Services;

/// <summary>
/// WASAPI microphone capture producing an in-memory 16 kHz / 16-bit / mono WAV.
///
/// Warm-start strategy: a single WasapiCapture instance is created and primed (one brief
/// start/stop cycle) at app launch, which forces device/driver initialization and JITs the
/// audio path. The instance is then kept and restarted per recording, so the hotkey-to-first-
/// sample latency stays well under 50 ms while the mic-in-use indicator remains off when idle.
///
/// The device is captured at its native shared-mode format and converted on the fly
/// (downmix to mono, WDL resample to 16 kHz) so only the small output WAV is buffered.
/// </summary>
public sealed class RecorderService : IRecorderService, IDisposable
{
    public static readonly TimeSpan MaxRecordingDuration = TimeSpan.FromMinutes(5);

    private readonly Logger _log;
    private readonly object _sync = new();
    private readonly ManualResetEventSlim _stopCompleted = new(false);

    private WasapiCapture? _capture;
    private MMDeviceEnumerator? _deviceEnumerator;
    private DeviceNotificationClient? _notificationClient;
    private volatile bool _defaultDeviceChanged;

    // Per-recording session (guarded by _sync).
    private BufferedWaveProvider? _buffered;
    private ISampleProvider? _pipeline;
    private MemoryStream? _wavStream;
    private WaveFileWriter? _writer;
    private System.Threading.Timer? _maxTimer;
    private volatile bool _recording;
    private volatile float _lastLevel;
    private readonly float[] _drainBuffer = new float[8192];

    public event Action? MaxDurationReached;
    public event Action<string>? Failed;

    public RecorderService(Logger log) => _log = log;

    /// <summary>Latest peak level (0..1) of the mono 16 kHz stream; polled by the overlay.</summary>
    public float CurrentLevel => _lastLevel;

    /// <summary>Initializes the capture device and runs one throwaway start/stop cycle.
    /// Call from a background thread (never the UI thread) at app start.</summary>
    public void Prime()
    {
        try
        {
            lock (_sync)
            {
                EnsureCapture();
            }
            _stopCompleted.Reset();
            _capture!.StartRecording();
            Thread.Sleep(150);
            _capture.StopRecording();
            _stopCompleted.Wait(TimeSpan.FromSeconds(1));
            _log.Info("Audio capture primed.");
        }
        catch (Exception ex)
        {
            // No microphone yet is not fatal; a real failure surfaces on first recording.
            _log.Warn("Audio capture priming failed.", ex);
            DisposeCapture();
        }
    }

    /// <summary>Begins recording. Throws when the capture device cannot be started.</summary>
    public void Start()
    {
        lock (_sync)
        {
            if (_recording)
            {
                return;
            }

            if (_defaultDeviceChanged)
            {
                _defaultDeviceChanged = false;
                DisposeCaptureLocked();
            }

            try
            {
                EnsureCapture();
            }
            catch (Exception ex)
            {
                _log.Error("No usable microphone.", ex);
                throw new InvalidOperationException("No microphone available.", ex);
            }

            var sourceFormat = _capture!.WaveFormat;
            _buffered = new BufferedWaveProvider(sourceFormat)
            {
                ReadFully = false, // never pad with silence; Read returns only real samples
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(5),
            };
            ISampleProvider samples = _buffered.ToSampleProvider();
            if (sourceFormat.Channels > 1)
            {
                samples = sourceFormat.Channels == 2
                    ? new StereoToMonoSampleProvider(samples) { LeftVolume = 0.5f, RightVolume = 0.5f }
                    : new AnyToMonoSampleProvider(samples);
            }
            _pipeline = new WdlResamplingSampleProvider(samples, 16000);

            _wavStream = new MemoryStream();
            _writer = new WaveFileWriter(new IgnoreDisposeStream(_wavStream), new WaveFormat(16000, 16, 1));
            _lastLevel = 0f;
            _stopCompleted.Reset();
            _recording = true;
        }

        try
        {
            _capture!.StartRecording();
        }
        catch (Exception ex)
        {
            _log.Error("Failed to start recording; retrying with a fresh device.", ex);
            lock (_sync)
            {
                DisposeCaptureLocked();
                try
                {
                    EnsureCapture();
                }
                catch (Exception retryEx)
                {
                    CleanupSessionLocked();
                    _recording = false;
                    throw new InvalidOperationException("No microphone available.", retryEx);
                }
            }
            try
            {
                _capture!.StartRecording();
            }
            catch (Exception retryEx)
            {
                lock (_sync)
                {
                    CleanupSessionLocked();
                    _recording = false;
                }
                throw new InvalidOperationException("Microphone could not be started.", retryEx);
            }
        }

        _maxTimer = new System.Threading.Timer(
            _ => MaxDurationReached?.Invoke(), null, MaxRecordingDuration, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Stops recording and returns the finished WAV, or null when nothing was captured.</summary>
    public byte[]? Stop() => StopCore(discard: false);

    /// <summary>Stops recording and discards the audio.</summary>
    public void Cancel() => StopCore(discard: true);

    private byte[]? StopCore(bool discard)
    {
        lock (_sync)
        {
            if (!_recording)
            {
                return null;
            }
            _recording = false; // DataAvailable stops writing from here on
            _maxTimer?.Dispose();
            _maxTimer = null;
        }

        try
        {
            _capture?.StopRecording();
            _stopCompleted.Wait(TimeSpan.FromMilliseconds(750));
        }
        catch (Exception ex)
        {
            _log.Warn("StopRecording failed.", ex);
        }

        lock (_sync)
        {
            byte[]? wav = null;
            if (!discard && _writer is not null && _wavStream is not null)
            {
                _writer.Dispose(); // finalizes the WAV header; IgnoreDisposeStream keeps the stream
                _writer = null;
                wav = _wavStream.ToArray();
            }
            CleanupSessionLocked();
            return wav;
        }
    }

    private void EnsureCapture()
    {
        if (_capture is not null)
        {
            return;
        }

        _capture = new WasapiCapture();
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;

        if (_deviceEnumerator is null)
        {
            try
            {
                _deviceEnumerator = new MMDeviceEnumerator();
                _notificationClient = new DeviceNotificationClient(this);
                _deviceEnumerator.RegisterEndpointNotificationCallback(_notificationClient);
            }
            catch (Exception ex)
            {
                _log.Warn("Device-change notifications unavailable.", ex);
            }
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (_sync)
        {
            if (!_recording || _buffered is null || _pipeline is null || _writer is null)
            {
                return; // priming or already stopped - discard
            }

            try
            {
                _buffered.AddSamples(e.Buffer, 0, e.BytesRecorded);
                var peak = _lastLevel * 0.6f; // gentle decay between buffers
                int read;
                while ((read = _pipeline.Read(_drainBuffer, 0, _drainBuffer.Length)) > 0)
                {
                    for (var i = 0; i < read; i++)
                    {
                        var abs = Math.Abs(_drainBuffer[i]);
                        if (abs > peak)
                        {
                            peak = abs;
                        }
                    }
                    _writer.WriteSamples(_drainBuffer, 0, read);
                }
                _lastLevel = Math.Min(1f, peak);
            }
            catch (Exception ex)
            {
                _log.Error("Audio conversion failed mid-recording.", ex);
            }
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        _stopCompleted.Set();

        if (e.Exception is null)
        {
            return;
        }

        _log.Error("Capture stopped with error.", e.Exception);
        var failedMidRecording = false;
        lock (_sync)
        {
            if (_recording)
            {
                failedMidRecording = true;
                _recording = false;
                _maxTimer?.Dispose();
                _maxTimer = null;
                CleanupSessionLocked();
            }
            DisposeCaptureLocked(); // recreate on next use
        }

        if (failedMidRecording)
        {
            Failed?.Invoke("Microphone was disconnected or stopped working.");
        }
    }

    private void OnDefaultDeviceChanged()
    {
        _defaultDeviceChanged = true;
        lock (_sync)
        {
            if (!_recording)
            {
                DisposeCaptureLocked();
                _defaultDeviceChanged = false;
            }
        }
        _log.Info("Default capture device changed.");
    }

    private void CleanupSessionLocked()
    {
        _writer?.Dispose();
        _writer = null;
        _wavStream?.Dispose();
        _wavStream = null;
        _pipeline = null;
        _buffered = null;
        _lastLevel = 0f;
    }

    private void DisposeCapture()
    {
        lock (_sync)
        {
            DisposeCaptureLocked();
        }
    }

    private void DisposeCaptureLocked()
    {
        if (_capture is null)
        {
            return;
        }
        try
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.Dispose();
        }
        catch (Exception ex)
        {
            _log.Warn("Capture dispose failed.", ex);
        }
        _capture = null;
    }

    public void Dispose()
    {
        try
        {
            Cancel();
        }
        catch
        {
        }
        lock (_sync)
        {
            if (_deviceEnumerator is not null && _notificationClient is not null)
            {
                try
                {
                    _deviceEnumerator.UnregisterEndpointNotificationCallback(_notificationClient);
                    _deviceEnumerator.Dispose();
                }
                catch
                {
                }
            }
            DisposeCaptureLocked();
        }
        _stopCompleted.Dispose();
    }

    /// <summary>Averages N channels into mono (StereoToMonoSampleProvider only handles 2).</summary>
    private sealed class AnyToMonoSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _channels;
        private float[] _sourceBuffer = [];

        public AnyToMonoSampleProvider(ISampleProvider source)
        {
            _source = source;
            _channels = source.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var needed = count * _channels;
            if (_sourceBuffer.Length < needed)
            {
                _sourceBuffer = new float[needed];
            }
            var read = _source.Read(_sourceBuffer, 0, needed);
            var frames = read / _channels;
            for (var f = 0; f < frames; f++)
            {
                var sum = 0f;
                for (var c = 0; c < _channels; c++)
                {
                    sum += _sourceBuffer[f * _channels + c];
                }
                buffer[offset + f] = sum / _channels;
            }
            return frames;
        }
    }

    private sealed class DeviceNotificationClient(RecorderService owner) : IMMNotificationClient
    {
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Capture && role == Role.Console)
            {
                owner.OnDefaultDeviceChanged();
            }
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
        }

        public void OnDeviceAdded(string pwstrDeviceId)
        {
        }

        public void OnDeviceRemoved(string deviceId)
        {
        }

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
        {
        }
    }
}
