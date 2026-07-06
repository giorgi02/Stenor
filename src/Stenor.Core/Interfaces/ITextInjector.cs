namespace Stenor.Interfaces;

/// <summary>
/// Delivers transcribed text into the focused application. Implemented in Stenor.App
/// (clipboard + Ctrl+V with a Unicode-typing fallback, which needs WPF clipboard access).
/// </summary>
public interface ITextInjector
{
    Task InjectAsync(string text, bool useUnicodeTyping);
}
