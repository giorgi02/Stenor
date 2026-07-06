using Stenor.Services;

namespace Stenor.Interfaces;

/// <summary>
/// UI-agnostic surface of the recording overlay. Implemented by the WPF overlay in Stenor.App;
/// lets <see cref="DictationController"/> drive visual state without referencing UI types.
/// Implementations must be callable from any thread.
/// </summary>
public interface IDictationOverlay
{
    /// <summary>Raised when the user dismisses the overlay (cancel recording/transcription).</summary>
    event Action? CancelRequested;

    void ShowRecording(Func<float> levelSource);

    void ShowTranscribing();

    void ShowDone();

    void ShowError(string message);

    void Hide();
}
