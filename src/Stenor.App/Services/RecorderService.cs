using System.IO;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Stenor.Constants;
using Stenor.Interfaces;

namespace Stenor.Services;

/// <summary>
/// WASAPI microphone capture producing an in-memory 16 kHz / 16-bit / mono WAV.
///
/// The capture stream is opened directly in the target format: shared-mode WASAPI inserts its
/// own channel matrixer and sample-rate converter (AutoConvertPcm + SrcDefaultQuality), so every
/// packet that arrives is already raw 16 kHz mono PCM16 and no managed conversion runs.
///
/// Warm-start strategy: a <see cref="WasapiRecorder"/> is single-use (its audio client can only
/// be initialized once), so one is always built ahead of time - at app launch, after a priming
/// start/stop cycle that forces device/driver initialization and JITs the audio path, and again
/// after every recording - and only started when the hotkey fires. Building is a cheap device
/// activation; the mic-in-use indicator stays off until the recorder actually starts.
/// </summary>
public sealed class RecorderService : IRecorderService, IDisposable
{
    public static readonly TimeSpan MaxRecordingDuration = TimeSpan.FromMinutes(5);

    private static readonly WaveFormat CaptureFormat =
        new(PcmFormat.SampleRateHz, PcmFormat.BitsPerSample, PcmFormat.ChannelCount);

    /// <summary>WASAPI hands over 5-10 ms packets; the live tap coalesces them into 50 ms chunks
    /// so each chunk is one WebSocket message rather than a hundred-plus per second.</summary>
    private const int PcmChunkBytes = PcmFormat.BytesPerSecond / 20;

    private readonly Logger _log;
    private readonly object _sync = new();
    private readonly ManualResetEventSlim _stopCompleted = new(false);

    private WasapiRecorder? _recorder; // armed (built, not started) or currently recording
    private MMDeviceEnumerator? _deviceEnumerator;
    private MMDeviceNotificationClient? _deviceNotifications;

    // Per-recording session (guarded by _sync).
    private MemoryStream? _wavStream;
    private WaveFileWriter? _waveWriter;
    private System.Threading.Timer? _maxDurationTimer;
    private volatile bool _recording;
    private volatile float _currentLevel;
    private readonly byte[] _pcmChunk = new byte[PcmChunkBytes];
    private int _pcmChunkFill;

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
            WasapiRecorder recorder;
            lock (_sync)
            {
                recorder = EnsureRecorder();
            }
            _stopCompleted.Reset();
            recorder.StartRecording();
            Thread.Sleep(150);
            StopAndWait(recorder, TimeSpan.FromSeconds(1));
            lock (_sync)
            {
                RetireRecorderLocked();
                EnsureRecorder(); // arm a fresh one for the first real recording
            }
            _log.Info("Audio capture primed.");
        }
        catch (Exception ex)
        {
            // No microphone yet is not fatal; a real failure surfaces on first recording.
            _log.Warn("Audio capture priming failed.", ex);
            lock (_sync)
            {
                RetireRecorderLocked();
            }
        }
    }

    /// <summary>Begins recording. Throws when the capture device cannot be started.</summary>
    public void Start()
    {
        WasapiRecorder recorder;
        lock (_sync)
        {
            if (_recording)
            {
                return;
            }

            try
            {
                recorder = EnsureRecorder();
            }
            catch (Exception ex)
            {
                _log.Error("No usable microphone.", ex);
                throw new InvalidOperationException("No microphone available.", ex);
            }

            _wavStream = new MemoryStream();
            _waveWriter = new WaveFileWriter(_wavStream, CaptureFormat);
            _currentLevel = 0f;
            _stopCompleted.Reset();
            _recording = true;
        }

        try
        {
            recorder.StartRecording();
        }
        catch (Exception ex)
        {
            _log.Error("Failed to start recording; retrying with a fresh device.", ex);
            lock (_sync)
            {
                RetireRecorderLocked();
                try
                {
                    recorder = EnsureRecorder();
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
                recorder.StartRecording();
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

    private byte[]? StopRecording(bool discardAudio, bool rearm = true)
    {
        WasapiRecorder? recorder;
        lock (_sync)
        {
            if (!_recording)
            {
                return null;
            }
            _recording = false; // DataAvailable stops writing from here on
            _maxDurationTimer?.Dispose();
            _maxDurationTimer = null;
            recorder = _recorder;
        }

        try
        {
            if (recorder is not null)
            {
                StopAndWait(recorder, TimeSpan.FromMilliseconds(750));
            }
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
                _waveWriter.Dispose(); // finalizes the WAV header; the stream is caller-owned and stays open
                _waveWriter = null;
                wav = _wavStream.ToArray();
            }
            if (!discardAudio)
            {
                FlushPcmChunkLocked(); // the tail reaches the tap before Stop returns
            }
            CleanupSessionLocked();
            RetireRecorderLocked(); // single-use: the next recording gets a fresh recorder
            if (rearm)
            {
                try
                {
                    EnsureRecorder();
                }
                catch (Exception ex)
                {
                    _log.Warn("Could not pre-arm the microphone; retried on the next recording.", ex);
                }
            }
            return wav;
        }
    }

    /// <summary>Requests a stop and waits (bounded) for the capture thread to wind down. A stop
    /// requested while the recorder is still <see cref="CaptureState.Starting"/> is lost - the
    /// capture thread overwrites the state once the device starts - so the request is held back
    /// until capture has actually begun (or failed).</summary>
    private void StopAndWait(WasapiRecorder recorder, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (recorder.CaptureState == CaptureState.Starting && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(5);
        }
        recorder.StopRecording();
        _stopCompleted.Wait(TimeSpan.FromMilliseconds(Math.Max(0, deadline - Environment.TickCount64)));
    }

    /// <summary>Returns the armed recorder, building one on the current default device.</summary>
    private WasapiRecorder EnsureRecorder()
    {
        if (_recorder is not null)
        {
            return _recorder;
        }

        var recorder = new WasapiRecorderBuilder().WithFormat(CaptureFormat).Build();
        recorder.DataAvailable += OnDataAvailable;
        recorder.RecordingStopped += OnRecordingStopped;
        _recorder = recorder;

        if (_deviceEnumerator is null)
        {
            try
            {
                _deviceEnumerator = new MMDeviceEnumerator();
                // Raised on the audio service's callback thread; the handler only swaps a field
                // and defers the dispose, as NAudio requires there.
                _deviceNotifications = _deviceEnumerator.CreateNotificationClient(useSynchronizationContext: false);
                _deviceNotifications.DefaultDeviceChanged += OnDefaultDeviceChanged;
            }
            catch (Exception ex)
            {
                _log.Warn("Device-change notifications unavailable.", ex);
            }
        }
        return recorder;
    }

    private void OnDataAvailable(
        ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        lock (_sync)
        {
            if (!_recording || _waveWriter is null)
            {
                return; // priming or already stopped - discard
            }

            try
            {
                _waveWriter.Write(buffer);

                var peakLevel = _currentLevel * 0.6f; // gentle decay between packets
                foreach (var sample in MemoryMarshal.Cast<byte, short>(buffer))
                {
                    var absoluteLevel = Math.Abs((int)sample) / 32768f;
                    if (absoluteLevel > peakLevel)
                    {
                        peakLevel = absoluteLevel;
                    }
                }
                _currentLevel = Math.Min(1f, peakLevel);

                if (PcmChunkAvailable is not null)
                {
                    var remaining = buffer;
                    while (!remaining.IsEmpty)
                    {
                        var take = Math.Min(remaining.Length, PcmChunkBytes - _pcmChunkFill);
                        remaining[..take].CopyTo(_pcmChunk.AsSpan(_pcmChunkFill));
                        _pcmChunkFill += take;
                        remaining = remaining[take..];
                        if (_pcmChunkFill == PcmChunkBytes)
                        {
                            FlushPcmChunkLocked();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error("Audio write failed mid-recording.", ex);
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
            RetireRecorderLocked(); // rebuilt on next use
        }

        if (failedMidRecording)
        {
            Failed?.Invoke("Microphone was disconnected or stopped working.");
        }
    }

    private void OnDefaultDeviceChanged(object? sender, DefaultDeviceChangedEventArgs e)
    {
        if (e.Flow != DataFlow.Capture || e.Role != Role.Console)
        {
            return;
        }
        lock (_sync)
        {
            // Armed on the old device: drop it so the next Start builds on the new default. A
            // recording in progress keeps its stream; the post-stop re-arm picks up the change.
            if (!_recording)
            {
                RetireRecorderLocked();
            }
        }
        _log.Info("Default capture device changed.");
    }

    private void FlushPcmChunkLocked()
    {
        if (_pcmChunkFill > 0 && PcmChunkAvailable is { } pcmChunkHandler)
        {
            pcmChunkHandler(_pcmChunk[.._pcmChunkFill]);
        }
        _pcmChunkFill = 0;
    }

    private void CleanupSessionLocked()
    {
        _waveWriter?.Dispose();
        _waveWriter = null;
        _wavStream?.Dispose();
        _wavStream = null;
        _currentLevel = 0f;
        _pcmChunkFill = 0;
    }

    /// <summary>Detaches the current recorder and disposes it off-thread: Dispose joins the
    /// capture thread, which must neither block a hotkey handler on a wedged driver nor run on
    /// the capture thread itself (RecordingStopped is raised there).</summary>
    private void RetireRecorderLocked()
    {
        var recorder = _recorder;
        if (recorder is null)
        {
            return;
        }
        _recorder = null;
        recorder.DataAvailable -= OnDataAvailable;
        recorder.RecordingStopped -= OnRecordingStopped;
        Task.Run(() =>
        {
            try
            {
                recorder.Dispose();
            }
            catch (Exception ex)
            {
                _log.Warn("Capture dispose failed.", ex);
            }
        });
    }

    public void Dispose()
    {
        try
        {
            StopRecording(discardAudio: true, rearm: false);
        }
        catch
        {
        }
        lock (_sync)
        {
            try
            {
                _deviceNotifications?.Dispose();
                _deviceEnumerator?.Dispose();
            }
            catch
            {
            }
            RetireRecorderLocked();
        }
        _stopCompleted.Dispose();
    }
}
