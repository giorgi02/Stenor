using System.Globalization;
using Stenor.Constants;

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
    private const int SamplesPerFrame = PcmFormat.SampleRateHz / 50; // 20 ms
    private const int FrameDurationMs = 20;

    /// <summary>Below ~200 ms there is not enough audio for percentiles to mean anything.</summary>
    private const int MinimumFrameCount = 10;

    /// <summary>~-56 dBFS. Guards a muted/dead mic; set low on purpose, since the dynamic-range
    /// test below - not loudness - is what actually rejects silence.</summary>
    private const double MinimumSignalPeak = 0.0015;

    /// <summary>~-66 dBFS. The floor is clamped up to this before deriving the voiced threshold,
    /// so a digitally silent lead-in cannot collapse the threshold to zero and mark every frame
    /// voiced.</summary>
    private const double NoiseEpsilon = 0.0005;

    /// <summary>~+9.5 dB of peak over floor. Measured room tone, fan noise and steady hum all
    /// land at 1.05-1.1x however loud they are, so the gap is wide and this sits deliberately on
    /// the permissive side of it to keep quiet speakers safe. Speech under ~9 dB above its own
    /// room floor is the known blind spot - unusable for the model anyway.</summary>
    private const double MinimumDynamicRange = 3.0;

    /// <summary>120 ms of sustained energy - one short word clears it, a single click does not.</summary>
    private const int MinimumVoicedFrameCount = 120 / FrameDurationMs;

    /// <summary>Verdict plus the measurements behind it, so a rejection can be tuned from the log.</summary>
    public readonly record struct AnalysisResult(
        bool HasSpeech, double NoiseFloor, double SignalPeak, int VoicedDurationMs)
    {
        public override string ToString() => string.Create(
            CultureInfo.InvariantCulture,
            $"peak {ToDecibelsFullScale(SignalPeak):F1} dBFS, floor {ToDecibelsFullScale(NoiseFloor):F1} dBFS, voiced {VoicedDurationMs} ms");

        private static double ToDecibelsFullScale(double level) =>
            20 * Math.Log10(Math.Max(level, 1e-6));
    }

    /// <summary>Analyzes a 16 kHz/16-bit/mono WAV produced by the recorder.</summary>
    public static AnalysisResult Analyze(byte[] wavData)
    {
        if (!TryFindDataChunk(wavData, out var dataOffset, out var dataLength))
        {
            return new AnalysisResult(
                HasSpeech: true, 0, 0, 0); // unreadable header - let Gemini decide
        }

        var frameCount = dataLength / 2 / SamplesPerFrame;
        if (frameCount < MinimumFrameCount)
        {
            return new AnalysisResult(HasSpeech: true, 0, 0, 0);
        }

        var rootMeanSquareLevels = new double[frameCount];
        for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            var frameOffset = dataOffset + frameIndex * SamplesPerFrame * 2;
            var squaredSampleSum = 0.0;
            for (var sampleIndex = 0; sampleIndex < SamplesPerFrame; sampleIndex++)
            {
                double sample = (short)(wavData[frameOffset + sampleIndex * 2]
                    | (wavData[frameOffset + sampleIndex * 2 + 1] << 8));
                squaredSampleSum += sample * sample;
            }
            rootMeanSquareLevels[frameIndex] =
                Math.Sqrt(squaredSampleSum / SamplesPerFrame) / 32768.0;
        }

        var sortedLevels = (double[])rootMeanSquareLevels.Clone();
        Array.Sort(sortedLevels);
        var noiseFloor = Percentile(sortedLevels, 0.10);
        var signalPeak = Percentile(sortedLevels, 0.90);

        // Geometric mean = the midpoint in dB between floor and peak. Roughly half the frames of
        // continuously spoken audio sit above it, so a pause-free clip still passes comfortably.
        var voicedThreshold = Math.Sqrt(Math.Max(noiseFloor, NoiseEpsilon) * signalPeak);
        var voicedFrameCount = 0;
        foreach (var level in rootMeanSquareLevels)
        {
            if (level >= voicedThreshold)
            {
                voicedFrameCount++;
            }
        }

        var hasSpeech = signalPeak >= MinimumSignalPeak
            && signalPeak >= noiseFloor * MinimumDynamicRange
            && voicedFrameCount >= MinimumVoicedFrameCount;
        return new AnalysisResult(
            hasSpeech, noiseFloor, signalPeak, voicedFrameCount * FrameDurationMs);
    }

    private static double Percentile(double[] sortedValues, double fraction) =>
        sortedValues[(int)(fraction * (sortedValues.Length - 1))];

    /// <summary>Walks the RIFF chunk list for "data" rather than assuming a 44-byte header.</summary>
    private static bool TryFindDataChunk(
        byte[] wavData, out int dataOffset, out int dataLength)
    {
        dataOffset = 0;
        dataLength = 0;
        if (wavData.Length < 44
            || !MatchesChunkId(wavData, 0, "RIFF")
            || !MatchesChunkId(wavData, 8, "WAVE"))
        {
            return false;
        }

        var chunkOffset = 12;
        while (chunkOffset + 8 <= wavData.Length)
        {
            var chunkSize = BitConverter.ToInt32(wavData, chunkOffset + 4);
            if (chunkSize < 0)
            {
                return false;
            }
            if (MatchesChunkId(wavData, chunkOffset, "data"))
            {
                dataOffset = chunkOffset + 8;
                dataLength = Math.Min(chunkSize, wavData.Length - dataOffset);
                return dataLength > 0;
            }
            chunkOffset += 8 + chunkSize + (chunkSize & 1); // chunks are word-aligned
        }
        return false;
    }

    private static bool MatchesChunkId(byte[] wavData, int offset, string chunkId) =>
        wavData[offset] == chunkId[0] && wavData[offset + 1] == chunkId[1]
        && wavData[offset + 2] == chunkId[2] && wavData[offset + 3] == chunkId[3];
}
