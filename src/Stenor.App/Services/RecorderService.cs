using System.IO;
using NAudio.CoreAudioApi;
using Stenor.Constants;
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

    private WasapiCapture? _audioCapture;
    private MMDeviceEnumerator? _deviceEnumerator;
    private DeviceNotificationClient? _deviceNotificationClient;
    private volatile bool _defaultDeviceChanged;

    // Per-recording session (guarded by _sync).
    private BufferedWaveProvider? _captureBuffer;
    private ISampleProvider? _resamplingPipeline;
    private MemoryStream? _wavStream;
    private WaveFileWriter? _waveWriter;
    private System.Threading.Timer? _maxDurationTimer;
    private volatile bool _recording;
    private volatile float _currentLevel;
    private readonly float[] _sampleBuffer = new float[8192];

    public event Action? MaxDurationReached;
    public event Action<string>? Failed;
    public event Action<byte[]>? PcmChunkAvailable;

    public RecorderService(Logger log) => _log = log;

    /// <summary>Latest peak level (0..1) of the mono 16 kHz stream; polled by the overlay.</summary>
    public float CurrentLevel => _currentLevel;

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
            _audioCapture!.StartRecording();
            Thread.Sleep(150);
            _audioCapture.StopRecording();
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

            var sourceFormat = _audioCapture!.WaveFormat;
            _captureBuffer = new BufferedWaveProvider(sourceFormat)
            {
                ReadFully = false, // never pad with silence; Read returns only real samples
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(5),
            };
            ISampleProvider samples = _captureBuffer.ToSampleProvider();
            if (sourceFormat.Channels > 1)
            {
                samples = sourceFormat.Channels == 2
                    ? new StereoToMonoSampleProvider(samples) { LeftVolume = 0.5f, RightVolume = 0.5f }
                    : new MultichannelToMonoSampleProvider(samples);
            }
            _resamplingPipeline = new WdlResamplingSampleProvider(samples, PcmFormat.SampleRateHz);

            _wavStream = new MemoryStream();
            _waveWriter = new WaveFileWriter(new IgnoreDisposeStream(_wavStream),
                new WaveFormat(PcmFormat.SampleRateHz, PcmFormat.BitsPerSample, PcmFormat.ChannelCount));
            _currentLevel = 0f;
            _stopCompleted.Reset();
            _recording = true;
        }

        try
        {
            _audioCapture!.StartRecording();
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
                _audioCapture!.StartRecording();
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

        _maxDurationTimer = new System.Threading.Timer(
            _ => MaxDurationReached?.Invoke(), null, MaxRecordingDuration, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Stops recording and returns the finished WAV, or null when nothing was captured.</summary>
    public byte[]? Stop() => StopRecording(discardAudio: false);

    /// <summary>Stops recording and discards the audio.</summary>
    public void Cancel() => StopRecording(discardAudio: true);

    private byte[]? StopRecording(bool discardAudio)
    {
        lock (_sync)
        {
            if (!_recording)
            {
                return null;
            }
            _recording = false; // DataAvailable stops writing from here on
            _maxDurationTimer?.Dispose();
            _maxDurationTimer = null;
        }

        try
        {
            _audioCapture?.StopRecording();
            _stopCompleted.Wait(TimeSpan.FromMilliseconds(750));
        }
        catch (Exception ex)
        {
            _log.Warn("StopRecording failed.", ex);
        }

        lock (_sync)
        {
            byte[]? wav = null;
            if (!discardAudio && _waveWriter is not null && _wavStream is not null)
            {
                _waveWriter.Dispose(); // finalizes the WAV header; IgnoreDisposeStream keeps the stream
                _waveWriter = null;
                wav = _wavStream.ToArray();
            }
            CleanupSessionLocked();
            return wav;
        }
    }

    private void EnsureCapture()
    {
        if (_audioCapture is not null)
        {
            return;
        }

        _audioCapture = new WasapiCapture();
        _audioCapture.DataAvailable += OnDataAvailable;
        _audioCapture.RecordingStopped += OnRecordingStopped;

        if (_deviceEnumerator is null)
        {
            try
            {
                _deviceEnumerator = new MMDeviceEnumerator();
                _deviceNotificationClient = new DeviceNotificationClient(this);
                _deviceEnumerator.RegisterEndpointNotificationCallback(_deviceNotificationClient);
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
            if (!_recording || _captureBuffer is null
                || _resamplingPipeline is null || _waveWriter is null)
            {
                return; // priming or already stopped - discard
            }

            try
            {
                _captureBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
                var peakLevel = _currentLevel * 0.6f; // gentle decay between buffers
                int samplesRead;
                while ((samplesRead = _resamplingPipeline.Read(
                    _sampleBuffer, 0, _sampleBuffer.Length)) > 0)
                {
                    for (var i = 0; i < samplesRead; i++)
                    {
                        var absoluteLevel = Math.Abs(_sampleBuffer[i]);
                        if (absoluteLevel > peakLevel)
                        {
                            peakLevel = absoluteLevel;
                        }
                    }
                    _waveWriter.WriteSamples(_sampleBuffer, 0, samplesRead);

                    if (PcmChunkAvailable is { } pcmChunkHandler)
                    {
                        var pcmBytes = new byte[samplesRead * 2];
                        for (var i = 0; i < samplesRead; i++)
                        {
                            var sample = (short)Math.Clamp(
                                (int)(_sampleBuffer[i] * 32767f), short.MinValue, short.MaxValue);
                            pcmBytes[i * 2] = (byte)sample;
                            pcmBytes[i * 2 + 1] = (byte)(sample >> 8);
                        }
                        pcmChunkHandler(pcmBytes);
                    }
                }
                _currentLevel = Math.Min(1f, peakLevel);
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
                _maxDurationTimer?.Dispose();
                _maxDurationTimer = null;
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
        _waveWriter?.Dispose();
        _waveWriter = null;
        _wavStream?.Dispose();
        _wavStream = null;
        _resamplingPipeline = null;
        _captureBuffer = null;
        _currentLevel = 0f;
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
        if (_audioCapture is null)
        {
            return;
        }
        try
        {
            _audioCapture.DataAvailable -= OnDataAvailable;
            _audioCapture.RecordingStopped -= OnRecordingStopped;
            _audioCapture.Dispose();
        }
        catch (Exception ex)
        {
            _log.Warn("Capture dispose failed.", ex);
        }
        _audioCapture = null;
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
            if (_deviceEnumerator is not null && _deviceNotificationClient is not null)
            {
                try
                {
                    _deviceEnumerator.UnregisterEndpointNotificationCallback(_deviceNotificationClient);
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
    private sealed class MultichannelToMonoSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _channelCount;
        private float[] _sourceBuffer = [];

        public MultichannelToMonoSampleProvider(ISampleProvider source)
        {
            _source = source;
            _channelCount = source.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var requiredSampleCount = count * _channelCount;
            if (_sourceBuffer.Length < requiredSampleCount)
            {
                _sourceBuffer = new float[requiredSampleCount];
            }
            var samplesRead = _source.Read(_sourceBuffer, 0, requiredSampleCount);
            var frameCount = samplesRead / _channelCount;
            for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                var sum = 0f;
                for (var channelIndex = 0; channelIndex < _channelCount; channelIndex++)
                {
                    sum += _sourceBuffer[frameIndex * _channelCount + channelIndex];
                }
                buffer[offset + frameIndex] = sum / _channelCount;
            }
            return frameCount;
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
