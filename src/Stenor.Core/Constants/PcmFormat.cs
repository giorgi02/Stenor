namespace Stenor.Constants;

/// <summary>The PCM format shared by recording, speech detection, and live transcription.</summary>
public static class PcmFormat
{
    public const int SampleRateHz = 16000;
    public const int BitsPerSample = 16;
    public const int ChannelCount = 1;
    public const int BytesPerSecond = SampleRateHz * BitsPerSample / 8 * ChannelCount;
    public static readonly string MimeType = $"audio/pcm;rate={SampleRateHz}";
}
