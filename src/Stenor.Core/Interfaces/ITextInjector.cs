namespace Stenor.Interfaces;

/// <summary>
/// Delivers transcribed text into the focused application. Implemented in Stenor.App
/// (clipboard + Ctrl+V with a Unicode-typing fallback, which needs WPF clipboard access).
/// </summary>
public interface ITextInjector
{
    /// <exception cref="TextInjectionException">Input was blocked or partly delivered;
    /// the user-facing message describes whether a clipboard recovery copy is available.</exception>
    Task InjectAsync(string text, bool useUnicodeTyping);
}

/// <summary>Input was blocked or only partly delivered. The message explains recovery;
/// callers must not retry a whole dictation when some input may already have landed.</summary>
public sealed class TextInjectionException(string message, bool mayHaveInjectedText) : Exception(message)
{
    public bool MayHaveInjectedText { get; } = mayHaveInjectedText;
}
