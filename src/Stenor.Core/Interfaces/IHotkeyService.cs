using Stenor.Services;

namespace Stenor.Interfaces;

/// <summary>
/// Hotkey press/release notifications consumed by <see cref="DictationController"/>.
/// Implemented in Stenor.App by the Win32 low-level keyboard hook.
/// </summary>
public interface IHotkeyService
{
    event Action? Pressed;

    /// <summary>Raised on release with how long the hotkey was held.</summary>
    event Action<TimeSpan>? Released;
}
