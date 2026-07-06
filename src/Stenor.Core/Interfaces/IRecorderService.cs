using Stenor.Services;

namespace Stenor.Interfaces;

/// <summary>
/// Microphone capture surface consumed by <see cref="DictationController"/>. Implemented in
/// Stenor.App on top of WASAPI. Implementations must be callable from any thread.
/// </summary>
public interface IRecorderService
{
    /// <summary>Raised when the recording-length cap is hit; the controller auto-stops.</summary>
    event Action? MaxDurationReached;

    /// <summary>Raised when capture dies mid-recording; carries a user-presentable message.</summary>
    event Action<string>? Failed;

    /// <summary>Current input level in [0..1], for the overlay's live meter.</summary>
    float CurrentLevel { get; }

    void Start();

    /// <summary>Stops capture and returns the recorded WAV, or null when nothing was captured.</summary>
    byte[]? Stop();

    void Cancel();
}
