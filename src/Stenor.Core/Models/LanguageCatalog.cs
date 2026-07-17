namespace Stenor.Models;

/// <summary>
/// The 97 languages the Gemini Live API officially supports for speech
/// (https://ai.google.dev/gemini-api/docs/live-api/capabilities), alphabetically sorted.
/// Names are stored in settings and interpolated into transcription prompts as-is; the ISO-639
/// codes are passed as ASR language hints on the live path (accepted by the consumer API and
/// verified to fix Georgian misrecognition, 2026-07).
/// </summary>
public static class LanguageCatalog
{
    private static readonly (string Name, string Code)[] Entries =
    [
        ("Afrikaans", "af"), ("Akan", "ak"), ("Albanian", "sq"), ("Amharic", "am"),
        ("Arabic", "ar"), ("Armenian", "hy"), ("Assamese", "as"), ("Azerbaijani", "az"),
        ("Basque", "eu"), ("Belarusian", "be"), ("Bengali", "bn"), ("Bosnian", "bs"),
        ("Bulgarian", "bg"), ("Burmese", "my"), ("Catalan", "ca"), ("Cebuano", "ceb"),
        ("Chinese", "zh"), ("Croatian", "hr"), ("Czech", "cs"), ("Danish", "da"),
        ("Dutch", "nl"), ("English", "en"), ("Estonian", "et"), ("Faroese", "fo"),
        ("Filipino", "fil"), ("Finnish", "fi"), ("French", "fr"), ("Galician", "gl"),
        ("Georgian", "ka"), ("German", "de"), ("Greek", "el"), ("Gujarati", "gu"),
        ("Hausa", "ha"), ("Hebrew", "he"), ("Hindi", "hi"), ("Hungarian", "hu"),
        ("Icelandic", "is"), ("Indonesian", "id"), ("Irish", "ga"), ("Italian", "it"),
        ("Japanese", "ja"), ("Kannada", "kn"), ("Kazakh", "kk"), ("Khmer", "km"),
        ("Kinyarwanda", "rw"), ("Korean", "ko"), ("Kurdish", "ku"), ("Kyrgyz", "ky"),
        ("Lao", "lo"), ("Latvian", "lv"), ("Lithuanian", "lt"), ("Macedonian", "mk"),
        ("Malay", "ms"), ("Malayalam", "ml"), ("Maltese", "mt"), ("Maori", "mi"),
        ("Marathi", "mr"), ("Mongolian", "mn"), ("Nepali", "ne"), ("Norwegian", "no"),
        ("Odia", "or"), ("Oromo", "om"), ("Pashto", "ps"), ("Persian", "fa"),
        ("Polish", "pl"), ("Portuguese", "pt"), ("Punjabi", "pa"), ("Quechua", "qu"),
        ("Romanian", "ro"), ("Romansh", "rm"), ("Russian", "ru"), ("Serbian", "sr"),
        ("Sindhi", "sd"), ("Sinhala", "si"), ("Slovak", "sk"), ("Slovenian", "sl"),
        ("Somali", "so"), ("Southern Sotho", "st"), ("Spanish", "es"), ("Swahili", "sw"),
        ("Swedish", "sv"), ("Tajik", "tg"), ("Tamil", "ta"), ("Telugu", "te"),
        ("Thai", "th"), ("Tswana", "tn"), ("Turkish", "tr"), ("Turkmen", "tk"),
        ("Ukrainian", "uk"), ("Urdu", "ur"), ("Uzbek", "uz"), ("Vietnamese", "vi"),
        ("Welsh", "cy"), ("Western Frisian", "fy"), ("Wolof", "wo"), ("Yoruba", "yo"),
        ("Zulu", "zu"),
    ];

    public static readonly IReadOnlyList<string> All = [.. Entries.Select(e => e.Name)];

    private static readonly Dictionary<string, string> Codes =
        Entries.ToDictionary(e => e.Name, e => e.Code, StringComparer.Ordinal);

    /// <summary>ISO-639 code for a catalog language name, or null for unknown names
    /// (settings may hold values from older versions).</summary>
    public static string? CodeFor(string name) => Codes.TryGetValue(name, out var code) ? code : null;
}
