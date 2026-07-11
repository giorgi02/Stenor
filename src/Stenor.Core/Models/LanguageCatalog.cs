namespace Stenor.Models;

/// <summary>
/// The 97 languages the Gemini Live API officially supports for speech
/// (https://ai.google.dev/gemini-api/docs/live-api/capabilities), alphabetically sorted.
/// Names are stored in settings and interpolated into transcription prompts as-is.
/// </summary>
public static class LanguageCatalog
{
    public static readonly IReadOnlyList<string> All =
    [
        "Afrikaans", "Akan", "Albanian", "Amharic", "Arabic", "Armenian", "Assamese",
        "Azerbaijani", "Basque", "Belarusian", "Bengali", "Bosnian", "Bulgarian", "Burmese",
        "Catalan", "Cebuano", "Chinese", "Croatian", "Czech", "Danish", "Dutch", "English",
        "Estonian", "Faroese", "Filipino", "Finnish", "French", "Galician", "Georgian",
        "German", "Greek", "Gujarati", "Hausa", "Hebrew", "Hindi", "Hungarian", "Icelandic",
        "Indonesian", "Irish", "Italian", "Japanese", "Kannada", "Kazakh", "Khmer",
        "Kinyarwanda", "Korean", "Kurdish", "Kyrgyz", "Lao", "Latvian", "Lithuanian",
        "Macedonian", "Malay", "Malayalam", "Maltese", "Maori", "Marathi", "Mongolian",
        "Nepali", "Norwegian", "Odia", "Oromo", "Pashto", "Persian", "Polish", "Portuguese",
        "Punjabi", "Quechua", "Romanian", "Romansh", "Russian", "Serbian", "Sindhi",
        "Sinhala", "Slovak", "Slovenian", "Somali", "Southern Sotho", "Spanish", "Swahili",
        "Swedish", "Tajik", "Tamil", "Telugu", "Thai", "Tswana", "Turkish", "Turkmen",
        "Ukrainian", "Urdu", "Uzbek", "Vietnamese", "Welsh", "Western Frisian", "Wolof",
        "Yoruba", "Zulu",
    ];
}
