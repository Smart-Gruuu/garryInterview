using System.Buffers.Binary;
using System.Runtime.InteropServices;
using InterviewAssist.Core.Audio;

namespace InterviewAssist.Core.Tests;

public class PcmConverterTests
{
    private const int OutRate = 16000;

    // Deliberately awkward chunk sizes (in frames), cycled until the signal is used up.
    private static readonly int[] OddChunkFrames = [1, 7, 441, 480, 13, 1023, 2, 999, 160, 3];

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-44100, 2)]
    [InlineData(48000, 0)]
    [InlineData(48000, -1)]
    public void Constructor_RejectsNonPositiveArguments(int rate, int channels)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PcmConverter(rate, channels));
    }

    [Fact]
    public void Constructor_ExposesInputFormat()
    {
        var converter = new PcmConverter(44100, 2);

        Assert.Equal(44100, converter.InputSampleRate);
        Assert.Equal(2, converter.InputChannels);
    }

    [Fact]
    public void PassThrough_16kMonoInt16_IsBitExact()
    {
        var random = new Random(1234);
        var input = new short[4000];
        for (int i = 0; i < input.Length; i++)
            input[i] = (short)random.Next(-32767, 32768);
        input[0] = 0;
        input[1] = short.MaxValue;
        input[2] = -short.MaxValue;

        var output = new PcmConverter(OutRate, 1).ConvertInt16(input);

        Assert.Equal(input, ToShorts(output));
    }

    [Fact]
    public void PassThrough_16kMonoFloat_RoundsToInt16()
    {
        float[] input = [0f, 0.5f, -0.5f, 0.25f, 1f, -1f, 0.00001f];

        var output = ToShorts(new PcmConverter(OutRate, 1).ConvertFloat32(input));

        short[] expected = [0, 16384, -16384, 8192, 32767, -32767, 0];
        Assert.Equal(expected, output);
    }

    [Fact]
    public void PassThrough_16kStereo_DownmixesByAveraging()
    {
        float[] input = [0.5f, -0.5f, 0.5f, 0.25f, -1f, -1f, 0.2f, 0.4f];

        var output = ToShorts(new PcmConverter(OutRate, 2).ConvertFloat32(input));

        short[] expected = [0, (short)Math.Round(0.375 * 32767), -32767, (short)Math.Round(0.3 * 32767)];
        Assert.Equal(expected.Length, output.Length);
        for (int i = 0; i < expected.Length; i++)
            Assert.InRange(output[i], expected[i] - 1, expected[i] + 1);
    }

    [Fact]
    public void Clipping_ClampsToInt16Range()
    {
        float[] input = [2f, -2f, 1.5f, -1.0001f, 1f, -1f, 100f];

        var output = ToShorts(new PcmConverter(OutRate, 1).ConvertFloat32(input));

        short[] expected = [32767, -32767, 32767, -32767, 32767, -32767, 32767];
        Assert.Equal(expected, output);
    }

    [Fact]
    public void Clipping_LoudSignalWhileDownsampling_NeverWraps()
    {
        var converter = new PcmConverter(48000, 2);
        var input = Sine(48000, 2, 1000, amplitude: 3f, seconds: 0.25);

        var output = ToShorts(converter.ConvertFloat32(input));

        Assert.NotEmpty(output);
        Assert.DoesNotContain(output, s => s == short.MinValue);
        // A wrapped (overflowed) sample would break up the clipped plateaus.
        Assert.True(output.Count(s => s == short.MaxValue) > output.Length / 4);
        Assert.True(output.Count(s => s == -short.MaxValue) > output.Length / 4);
    }

    [Fact]
    public void NonFiniteInput_BecomesSilence_AndDoesNotPoisonLaterAudio()
    {
        var converter = new PcmConverter(48000, 1);

        var bad = converter.ConvertFloat32([float.NaN, float.PositiveInfinity, float.NegativeInfinity, float.NaN]);
        Assert.All(ToShorts(bad), s => Assert.Equal(0, s));

        var good = converter.ConvertFloat32(Sine(48000, 1, 1000, 0.5f, 0.2));
        Assert.InRange(PcmConverter.ComputeLevel(good), 0.3f, 0.4f);
    }

    [Theory]
    [InlineData(44100, 2)]
    [InlineData(48000, 2)]
    [InlineData(96000, 2)]
    [InlineData(22050, 1)]
    [InlineData(32000, 1)]
    [InlineData(11025, 1)]
    [InlineData(8000, 1)]
    [InlineData(48000, 6)]
    public void OutputLength_TracksInputAcrossOddChunks(int rate, int channels)
    {
        var converter = new PcmConverter(rate, channels);
        var signal = Sine(rate, channels, 440, 0.5f, seconds: 3.1);
        long totalInFrames = 0;
        long totalOut = 0;
        int offset = 0;
        int chunkIndex = 0;

        while (offset < signal.Length)
        {
            int frames = OddChunkFrames[chunkIndex++ % OddChunkFrames.Length];
            int samples = Math.Min(frames * channels, signal.Length - offset);
            var output = converter.ConvertFloat32(signal.AsSpan(offset, samples));
            offset += samples;
            totalInFrames += samples / channels;
            totalOut += output.Length / 2;

            Assert.True(output.Length % 2 == 0);
            double expected = totalInFrames * (double)OutRate / rate;
            Assert.InRange(totalOut, expected - 1, expected + 1);
        }
    }

    [Theory]
    [InlineData(44100, 2)]
    [InlineData(48000, 2)]
    [InlineData(8000, 1)]
    [InlineData(OutRate, 2)]
    public void Chunking_DoesNotChangeTheOutput(int rate, int channels)
    {
        var signal = Sine(rate, channels, 1234, 0.6f, seconds: 1.3);

        var whole = new PcmConverter(rate, channels).ConvertFloat32(signal);

        var converter = new PcmConverter(rate, channels);
        var pieces = new List<byte>();
        int offset = 0;
        int chunkIndex = 0;
        while (offset < signal.Length)
        {
            int samples = Math.Min(OddChunkFrames[chunkIndex++ % OddChunkFrames.Length] * channels, signal.Length - offset);
            pieces.AddRange(converter.ConvertFloat32(signal.AsSpan(offset, samples)));
            offset += samples;
        }

        Assert.Equal(whole, pieces.ToArray());
    }

    [Theory]
    [InlineData(AudioSampleFormat.Float32)]
    [InlineData(AudioSampleFormat.Int16)]
    [InlineData(AudioSampleFormat.Int24)]
    [InlineData(AudioSampleFormat.Int32)]
    public void PartialFrames_AreCarriedToTheNextCall(AudioSampleFormat format)
    {
        const int rate = 44100;
        const int channels = 2;
        var bytes = Encode(Sine(rate, channels, 700, 0.5f, seconds: 0.5), format);

        var whole = new PcmConverter(rate, channels).ConvertBytes(bytes, format);

        var converter = new PcmConverter(rate, channels);
        var pieces = new List<byte>();
        int[] byteChunks = [1, 3, 5, 7, 11, 13, 1001, 2, 17, 4093];
        int offset = 0;
        int chunkIndex = 0;
        while (offset < bytes.Length)
        {
            int size = Math.Min(byteChunks[chunkIndex++ % byteChunks.Length], bytes.Length - offset);
            pieces.AddRange(converter.ConvertBytes(bytes.AsSpan(offset, size), format));
            offset += size;
        }

        Assert.NotEmpty(whole);
        Assert.Equal(whole, pieces.ToArray());
    }

    [Fact]
    public void PartialFrame_IsDroppedWhenTheFormatChanges()
    {
        var converter = new PcmConverter(OutRate, 1);

        Assert.Empty(converter.ConvertBytes([0x01], AudioSampleFormat.Int16));
        var output = converter.ConvertBytes(BitConverter.GetBytes(0.5f), AudioSampleFormat.Float32);

        Assert.Equal(new short[] { 16384 }, ToShorts(output));
    }

    [Fact]
    public void ConvertBytes_MatchesTypedOverloads()
    {
        var floats = Sine(48000, 2, 900, 0.7f, seconds: 0.3);
        var shorts = Array.ConvertAll(floats, f => (short)Math.Round(f * 32767));

        Assert.Equal(
            new PcmConverter(48000, 2).ConvertFloat32(floats),
            new PcmConverter(48000, 2).ConvertBytes(MemoryMarshal.AsBytes(floats.AsSpan()), AudioSampleFormat.Float32));
        Assert.Equal(
            new PcmConverter(48000, 2).ConvertInt16(shorts),
            new PcmConverter(48000, 2).ConvertBytes(MemoryMarshal.AsBytes(shorts.AsSpan()), AudioSampleFormat.Int16));
    }

    [Fact]
    public void Int24_DecodesLittleEndianSigned()
    {
        int[] values = [0, 8388607, -8388607, 4194304, -4194304, 1000];
        var bytes = new byte[values.Length * 3];
        for (int i = 0; i < values.Length; i++)
        {
            bytes[i * 3] = (byte)values[i];
            bytes[i * 3 + 1] = (byte)(values[i] >> 8);
            bytes[i * 3 + 2] = (byte)(values[i] >> 16);
        }

        var output = ToShorts(new PcmConverter(OutRate, 1).ConvertBytes(bytes, AudioSampleFormat.Int24));

        Assert.Equal(values.Length, output.Length);
        for (int i = 0; i < values.Length; i++)
        {
            double expected = values[i] / 8388607.0 * 32767;
            Assert.InRange(output[i], expected - 1, expected + 1);
        }
    }

    [Fact]
    public void Int24_MinimumValueClips()
    {
        byte[] bytes = [0x00, 0x00, 0x80]; // -8388608

        var output = ToShorts(new PcmConverter(OutRate, 1).ConvertBytes(bytes, AudioSampleFormat.Int24));

        Assert.Equal(new short[] { -32767 }, output);
    }

    [Fact]
    public void Int32_DecodesLittleEndianSigned()
    {
        int[] values = [0, int.MaxValue, -int.MaxValue, int.MinValue, 1 << 30, -(1 << 29)];
        var bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 4), values[i]);

        var output = ToShorts(new PcmConverter(OutRate, 1).ConvertBytes(bytes, AudioSampleFormat.Int32));

        short[] expected = [0, 32767, -32767, -32767, 16384, -8192];
        Assert.Equal(expected.Length, output.Length);
        for (int i = 0; i < expected.Length; i++)
            Assert.InRange(output[i], expected[i] - 1, expected[i] + 1);
    }

    [Fact]
    public void Int16_StereoDownmix_Averages()
    {
        short[] input = [10000, -10000, 20000, 10000, short.MaxValue, short.MaxValue];

        var output = ToShorts(new PcmConverter(OutRate, 2).ConvertInt16(input));

        Assert.Equal(new short[] { 0, 15000, short.MaxValue }, output);
    }

    [Fact]
    public void UnknownFormat_Throws()
    {
        var converter = new PcmConverter(48000, 2);

        Assert.Throws<ArgumentOutOfRangeException>(() => converter.ConvertBytes(new byte[16], (AudioSampleFormat)42));
    }

    [Fact]
    public void EmptyInput_ProducesEmptyOutput()
    {
        var converter = new PcmConverter(44100, 2);

        Assert.Empty(converter.ConvertFloat32([]));
        Assert.Empty(converter.ConvertInt16([]));
        Assert.Empty(converter.ConvertBytes([], AudioSampleFormat.Int24));
    }

    [Theory]
    [InlineData(44100, 2, 1000)]
    [InlineData(48000, 2, 1000)]
    [InlineData(48000, 1, 300)]
    [InlineData(44100, 2, 3000)]
    [InlineData(8000, 1, 1000)]
    [InlineData(96000, 2, 2500)]
    public void SineWave_FrequencyAndAmplitudeArePreserved(int rate, int channels, double frequency)
    {
        const float amplitude = 0.5f;
        var converter = new PcmConverter(rate, channels);
        var signal = Sine(rate, channels, frequency, amplitude, seconds: 2);
        var output = new List<short>();
        for (int offset = 0; offset < signal.Length;)
        {
            int samples = Math.Min(4410 * channels + channels, signal.Length - offset);
            output.AddRange(ToShorts(converter.ConvertFloat32(signal.AsSpan(offset, samples))));
            offset += samples;
        }

        // Skip the first 50 ms while the filter settles, then measure over exactly one second.
        var window = output.Skip(800).Take(OutRate).ToArray();
        Assert.Equal(OutRate, window.Length);

        int risingCrossings = 0;
        for (int i = 1; i < window.Length; i++)
        {
            if (window[i - 1] < 0 && window[i] >= 0)
                risingCrossings++;
        }
        Assert.InRange(risingCrossings, frequency * 0.99 - 1, frequency * 1.01 + 1);

        float level = PcmConverter.ComputeLevel(MemoryMarshal.AsBytes(window.AsSpan()));
        double expectedRms = amplitude / Math.Sqrt(2);
        Assert.InRange(level, expectedRms * 0.9, expectedRms * 1.1);
    }

    [Theory]
    [InlineData(48000, 12000)]   // would alias to 4 kHz
    [InlineData(44100, 11000)]   // would alias to 5 kHz
    [InlineData(48000, 15000)]   // would alias to 1 kHz
    public void Downsampling_SuppressesContentThatWouldAlias(int rate, double frequency)
    {
        var converter = new PcmConverter(rate, 1);
        var output = ToShorts(converter.ConvertFloat32(Sine(rate, 1, frequency, 0.5f, seconds: 1)));

        var settled = output.Skip(800).ToArray();
        float level = PcmConverter.ComputeLevel(MemoryMarshal.AsBytes(settled.AsSpan()));
        double inputRms = 0.5 / Math.Sqrt(2);
        Assert.True(level < inputRms * 0.1, $"aliased level {level:F4} vs input {inputRms:F4}");
    }

    [Fact]
    public void Silence_StaysExactlySilent()
    {
        var converter = new PcmConverter(44100, 2);

        var output = converter.ConvertFloat32(new float[44100 * 2]);

        Assert.Equal(16000 * 2, output.Length);
        Assert.All(output, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Silence_AfterSignalDecaysToExactZero()
    {
        var converter = new PcmConverter(48000, 2);
        converter.ConvertFloat32(Sine(48000, 2, 1000, 0.9f, seconds: 0.5));

        converter.ConvertFloat32(new float[48000 * 2]);
        var later = converter.ConvertFloat32(new float[4800 * 2]);

        Assert.NotEmpty(later);
        Assert.All(later, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Reset_ClearsAllStreamState()
    {
        var signal = Sine(44100, 2, 523, 0.5f, seconds: 0.4);
        var fresh = new PcmConverter(44100, 2).ConvertFloat32(signal);

        var reused = new PcmConverter(44100, 2);
        reused.ConvertFloat32(Sine(44100, 2, 880, 0.8f, seconds: 0.37));
        reused.ConvertBytes([1, 2, 3], AudioSampleFormat.Float32); // leaves a partial frame behind
        reused.Reset();

        Assert.Equal(fresh, reused.ConvertFloat32(signal));
    }

    [Fact]
    public void ComputeLevel_OfSilenceIsZero()
    {
        Assert.Equal(0f, PcmConverter.ComputeLevel(new byte[3200]));
        Assert.Equal(0f, PcmConverter.ComputeLevel([]));
        Assert.Equal(0f, PcmConverter.ComputeLevel([0x7F]));
    }

    [Fact]
    public void ComputeLevel_MeasuresRms()
    {
        var fullScale = new short[1000];
        for (int i = 0; i < fullScale.Length; i++)
            fullScale[i] = i % 2 == 0 ? short.MaxValue : (short)-short.MaxValue;
        Assert.Equal(1f, PcmConverter.ComputeLevel(MemoryMarshal.AsBytes(fullScale.AsSpan())), 3);

        var clipped = Enumerable.Repeat(short.MinValue, 100).ToArray();
        Assert.Equal(1f, PcmConverter.ComputeLevel(MemoryMarshal.AsBytes(clipped.AsSpan())));

        var sine = new short[16000];
        for (int i = 0; i < sine.Length; i++)
            sine[i] = (short)Math.Round(0.5 * 32767 * Math.Sin(2 * Math.PI * 440 * i / 16000));
        Assert.Equal(0.5 / Math.Sqrt(2), PcmConverter.ComputeLevel(MemoryMarshal.AsBytes(sine.AsSpan())), 3);
    }

    [Fact]
    public void ComputeLevel_IgnoresTrailingOddByte()
    {
        var bytes = new byte[5];
        BinaryPrimitives.WriteInt16LittleEndian(bytes, 16384);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(2), -16384);
        bytes[4] = 0xFF;

        Assert.Equal(16384f / 32767f, PcmConverter.ComputeLevel(bytes), 4);
    }

    private static float[] Sine(int rate, int channels, double frequency, float amplitude, double seconds)
    {
        int frames = (int)(rate * seconds);
        var samples = new float[frames * channels];
        for (int f = 0; f < frames; f++)
        {
            float value = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * f / rate));
            for (int c = 0; c < channels; c++)
                samples[f * channels + c] = value;
        }
        return samples;
    }

    private static byte[] Encode(float[] samples, AudioSampleFormat format)
    {
        switch (format)
        {
            case AudioSampleFormat.Float32:
                return MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
            case AudioSampleFormat.Int16:
            {
                var bytes = new byte[samples.Length * 2];
                for (int i = 0; i < samples.Length; i++)
                    BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), (short)Math.Round(samples[i] * 32767));
                return bytes;
            }
            case AudioSampleFormat.Int24:
            {
                var bytes = new byte[samples.Length * 3];
                for (int i = 0; i < samples.Length; i++)
                {
                    int v = (int)Math.Round(samples[i] * 8388607);
                    bytes[i * 3] = (byte)v;
                    bytes[i * 3 + 1] = (byte)(v >> 8);
                    bytes[i * 3 + 2] = (byte)(v >> 16);
                }
                return bytes;
            }
            case AudioSampleFormat.Int32:
            {
                var bytes = new byte[samples.Length * 4];
                for (int i = 0; i < samples.Length; i++)
                    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 4), (int)Math.Round(samples[i] * (double)int.MaxValue));
                return bytes;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    private static short[] ToShorts(byte[] pcm16)
    {
        Assert.True(pcm16.Length % 2 == 0, "PCM16 output must have an even byte count");
        var result = new short[pcm16.Length / 2];
        for (int i = 0; i < result.Length; i++)
            result[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm16.AsSpan(i * 2));
        return result;
    }
}
