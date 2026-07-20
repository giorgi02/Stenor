using System.Globalization;

namespace Stenor.Services;

/// <summary>
/// Deterministic "does this recording contain speech?" gate, run before any Gemini call.
///
/// Transcription models hallucinate fluent, plausible text when handed silence - an accidental
/// hotkey tap used to produce a pasted greeting nobody said. A prompt rule ("silence = empty
/// output") is not reliable enough, so silence is rejected here instead of being uploaded.
///
/// The test is dynamic range, not loudness: silence is *flat* while speech swings between quiet
/// and loud, which holds at any mic gain. Frame energies are reduced to a robust floor/peak pair
/// (10th/90th percentile), and speech is declared only when the two are far enough apart, the
/// peak is above the noise of a muted device, and the loud part lasts long enough to be a word
/// rather than a keyboard click.
///
/// Deliberately fails open: anything that cannot be measured confidently is reported as speech,
/// because losing a real dictation is worse than one stray hallucination.
/// </summary>
public static class SpeechDetector
{
    private const int SampleRate = 16000; // RecorderService always writes 16 kHz/16-bit/mono
    private const int FrameSamples = SampleRate / 50; // 20 ms
    private const int FrameMs = 20;

    /// <summary>Below ~200 ms there is not enough audio for percentiles to mean anything.</summary>
    private const int MinFrames = 10;

    /// <summary>~-56 dBFS. Guards a muted/dead mic; set low on purpose, since the dynamic-range
    /// test below - not loudness - is what actually rejects silence.</summary>
    private const double MinPeak = 0.0015;

    /// <summary>~-66 dBFS. The floor is clamped up to this before deriving the voiced threshold,
    /// so a digitally silent lead-in cannot collapse the threshold to zero and mark every frame
    /// voiced.</summary>
    private const double NoiseEpsilon = 0.0005;

    /// <summary>~+9.5 dB of peak over floor. Measured room tone, fan noise and steady hum all
    /// land at 1.05-1.1x however loud they are, so the gap is wide and this sits deliberately on
    /// the permissive side of it to keep quiet speakers safe. Speech under ~9 dB above its own
    /// room floor is the known blind spot - unusable for the model anyway.</summary>
    private const double MinDynamicRange = 3.0;

    /// <summary>120 ms of sustained energy - one short word clears it, a single click does not.</summary>
    private const int MinVoicedFrames = 120 / FrameMs;

    /// <summary>Verdict plus the measurements behind it, so a rejection can be tuned from the log.</summary>
    public readonly record struct Result(bool HasSpeech, double Floor, double Peak, int VoicedMs)
    {
        public override string ToString() => string.Create(
            CultureInfo.InvariantCulture,
            $"peak {Dbfs(Peak):F1} dBFS, floor {Dbfs(Floor):F1} dBFS, voiced {VoicedMs} ms");

        private static double Dbfs(double level) => 20 * Math.Log10(Math.Max(level, 1e-6));
    }

    /// <summary>Analyzes a 16 kHz/16-bit/mono WAV produced by the recorder.</summary>
    public static Result Analyze(byte[] wav)
    {
        if (!TryFindDataChunk(wav, out var start, out var length))
        {
            return new Result(HasSpeech: true, 0, 0, 0); // unreadable header - let Gemini decide
        }

        var frameCount = length / 2 / FrameSamples;
        if (frameCount < MinFrames)
        {
            return new Result(HasSpeech: true, 0, 0, 0);
        }

        var rms = new double[frameCount];
        for (var f = 0; f < frameCount; f++)
        {
            var offset = start + f * FrameSamples * 2;
            var sum = 0.0;
            for (var i = 0; i < FrameSamples; i++)
            {
                double sample = (short)(wav[offset + i * 2] | (wav[offset + i * 2 + 1] << 8));
                sum += sample * sample;
            }
            rms[f] = Math.Sqrt(sum / FrameSamples) / 32768.0;
        }

        var sorted = (double[])rms.Clone();
        Array.Sort(sorted);
        var floor = Percentile(sorted, 0.10);
        var peak = Percentile(sorted, 0.90);

        // Geometric mean = the midpoint in dB between floor and peak. Roughly half the frames of
        // continuously spoken audio sit above it, so a pause-free clip still passes comfortably.
        var threshold = Math.Sqrt(Math.Max(floor, NoiseEpsilon) * peak);
        var voiced = 0;
        foreach (var level in rms)
        {
            if (level >= threshold)
            {
                voiced++;
            }
        }

        var hasSpeech = peak >= MinPeak
            && peak >= floor * MinDynamicRange
            && voiced >= MinVoicedFrames;
        return new Result(hasSpeech, floor, peak, voiced * FrameMs);
    }

    private static double Percentile(double[] sorted, double fraction) =>
        sorted[(int)(fraction * (sorted.Length - 1))];

    /// <summary>Walks the RIFF chunk list for "data" rather than assuming a 44-byte header.</summary>
    private static bool TryFindDataChunk(byte[] wav, out int start, out int length)
    {
        start = 0;
        length = 0;
        if (wav.Length < 44 || !Matches(wav, 0, "RIFF") || !Matches(wav, 8, "WAVE"))
        {
            return false;
        }

        var pos = 12;
        while (pos + 8 <= wav.Length)
        {
            var size = BitConverter.ToInt32(wav, pos + 4);
            if (size < 0)
            {
                return false;
            }
            if (Matches(wav, pos, "data"))
            {
                start = pos + 8;
                length = Math.Min(size, wav.Length - start);
                return length > 0;
            }
            pos += 8 + size + (size & 1); // chunks are word-aligned
        }
        return false;
    }

    private static bool Matches(byte[] wav, int offset, string id) =>
        wav[offset] == id[0] && wav[offset + 1] == id[1]
        && wav[offset + 2] == id[2] && wav[offset + 3] == id[3];
}
