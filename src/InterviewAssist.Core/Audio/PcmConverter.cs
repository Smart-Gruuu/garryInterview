using System.Runtime.InteropServices;
using InterviewAssist.Core.Models;

namespace InterviewAssist.Core.Audio;

/// <summary>Sample encodings a capture device can deliver (interleaved, little-endian).</summary>
public enum AudioSampleFormat
{
    Float32,
    Int16,
    Int24,
    Int32,
}

/// <summary>
/// Converts interleaved device audio of any sample rate and channel count into the transport format
/// (<see cref="AudioFormat"/>: PCM16 little-endian, mono, 16 kHz).
/// Stateful: use one instance per stream so chunk boundaries stay seamless (filter memory, fractional resampler
/// position and incomplete trailing frames all carry over to the next call). Not thread-safe.
/// </summary>
public sealed class PcmConverter
{
    private const int OutputRate = AudioFormat.SampleRate;
    private const int MaxChannels = 1024;
    private const int MaxBytesPerSample = 4;

    // Anti-alias low-pass used when downsampling: 4th-order Butterworth (two biquads) at 0.4 × 16 kHz = 6.4 kHz.
    // Keeps the speech band flat and removes most of what would otherwise fold back below 8 kHz.
    private const double CutoffHz = OutputRate * 0.4;
    private const double ButterworthQ1 = 0.54119610014619701;   // 1 / (2 cos(π/8))
    private const double ButterworthQ2 = 1.3065629648763764;    // 1 / (2 cos(3π/8))

    private readonly Biquad[] _lowPass;
    private readonly bool _passThrough;

    // Resampler step (input samples per output sample) as an exact fraction _stepNum / _phaseDen,
    // so the read position never drifts no matter how long the stream runs.
    private readonly int _stepNum;
    private readonly int _stepInt;
    private readonly int _stepFrac;
    private readonly int _phaseDen;

    private readonly byte[] _carry;          // incomplete trailing frame from the previous call
    private int _carryCount;
    private AudioSampleFormat _carryFormat;

    private float _last;                     // last (filtered) input sample of the previous call
    private int _pos;                        // integer read position of the next output, relative to _last
    private int _phase;                      // fractional read position in units of 1 / _phaseDen

    private float[] _work = [];              // [_last, mono samples of this call...]
    private byte[] _join = [];               // carry + new data, only used when a frame was split across calls

    public PcmConverter(int inputSampleRate, int inputChannels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inputSampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inputChannels);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(inputChannels, MaxChannels);

        InputSampleRate = inputSampleRate;
        InputChannels = inputChannels;
        _carry = new byte[MaxBytesPerSample * inputChannels];
        _passThrough = inputSampleRate == OutputRate;

        int gcd = GreatestCommonDivisor(inputSampleRate, OutputRate);
        _stepNum = inputSampleRate / gcd;
        _phaseDen = OutputRate / gcd;
        _stepInt = _stepNum / _phaseDen;
        _stepFrac = _stepNum % _phaseDen;

        _lowPass = inputSampleRate > OutputRate
            ? [new Biquad(inputSampleRate, CutoffHz, ButterworthQ1), new Biquad(inputSampleRate, CutoffHz, ButterworthQ2)]
            : [];
    }

    public int InputSampleRate { get; }

    public int InputChannels { get; }

    /// <summary>Converts interleaved 32-bit float samples (nominal range -1..1). Non-finite samples become silence.</summary>
    public byte[] ConvertFloat32(ReadOnlySpan<float> interleaved) =>
        Convert(MemoryMarshal.AsBytes(interleaved), AudioSampleFormat.Float32);

    /// <summary>Converts interleaved signed 16-bit samples.</summary>
    public byte[] ConvertInt16(ReadOnlySpan<short> interleaved) =>
        Convert(MemoryMarshal.AsBytes(interleaved), AudioSampleFormat.Int16);

    /// <summary>
    /// Converts a raw little-endian device buffer. A trailing partial frame is kept and completed by the next call
    /// with the same format.
    /// </summary>
    public byte[] ConvertBytes(ReadOnlySpan<byte> data, AudioSampleFormat format) => Convert(data, format);

    /// <summary>Forgets all stream state (filter memory, resampler position, partial frame), e.g. after a gap.</summary>
    public void Reset()
    {
        foreach (var filter in _lowPass)
            filter.Reset();

        _last = 0f;
        _pos = 0;
        _phase = 0;
        _carryCount = 0;
    }

    /// <summary>RMS level of PCM16 little-endian audio, 0..1. A trailing odd byte is ignored.</summary>
    public static float ComputeLevel(ReadOnlySpan<byte> pcm16)
    {
        var samples = MemoryMarshal.Cast<byte, short>(pcm16);
        if (samples.IsEmpty)
            return 0f;

        double sumOfSquares = 0;
        foreach (short sample in samples)
            sumOfSquares += (double)sample * sample;

        double rms = Math.Sqrt(sumOfSquares / samples.Length) / short.MaxValue;
        return (float)Math.Min(1.0, rms);
    }

    private byte[] Convert(ReadOnlySpan<byte> data, AudioSampleFormat format)
    {
        int frameBytes = BytesPerSample(format) * InputChannels;

        // A partial frame of a different format can't be completed by this data.
        if (_carryCount > 0 && format != _carryFormat)
            _carryCount = 0;
        _carryFormat = format;

        var input = data;
        if (_carryCount > 0)
        {
            // Rare (WASAPI delivers whole frames): join into one aligned buffer so frames stay contiguous.
            int total = _carryCount + data.Length;
            if (_join.Length < total)
                _join = new byte[Math.Max(total, _join.Length * 2)];
            _carry.AsSpan(0, _carryCount).CopyTo(_join);
            data.CopyTo(_join.AsSpan(_carryCount));
            input = _join.AsSpan(0, total);
        }

        int frames = input.Length / frameBytes;
        int wholeBytes = frames * frameBytes;
        var remainder = input[wholeBytes..];
        remainder.CopyTo(_carry);
        _carryCount = remainder.Length;

        if (frames == 0)
            return [];

        if (_work.Length < frames + 1)
            _work = new float[Math.Max(frames + 1, _work.Length * 2)];

        var mono = _work.AsSpan(1, frames);
        Downmix(input[..wholeBytes], format, mono);

        if (_passThrough)
            return Encode(mono);

        foreach (var filter in _lowPass)
            filter.Process(mono);

        _work[0] = _last;
        var result = Resample(_work.AsSpan(0, frames + 1));
        _last = _work[frames];
        return result;
    }

    private void Downmix(ReadOnlySpan<byte> frames, AudioSampleFormat format, Span<float> mono)
    {
        int channels = InputChannels;
        switch (format)
        {
            case AudioSampleFormat.Float32:
            {
                var samples = MemoryMarshal.Cast<byte, float>(frames);
                float scale = 1f / channels;
                for (int frame = 0, i = 0; frame < mono.Length; frame++)
                {
                    float sum = 0f;
                    for (int c = 0; c < channels; c++)
                        sum += samples[i++];
                    float value = sum * scale;
                    // A NaN/Infinity would poison the filter state for the rest of the stream.
                    mono[frame] = float.IsFinite(value) ? value : 0f;
                }
                break;
            }
            case AudioSampleFormat.Int16:
            {
                var samples = MemoryMarshal.Cast<byte, short>(frames);
                float scale = 1f / (short.MaxValue * (float)channels);
                for (int frame = 0, i = 0; frame < mono.Length; frame++)
                {
                    int sum = 0;
                    for (int c = 0; c < channels; c++)
                        sum += samples[i++];
                    mono[frame] = sum * scale;
                }
                break;
            }
            case AudioSampleFormat.Int24:
            {
                const int int24Max = (1 << 23) - 1;
                double scale = 1.0 / ((double)int24Max * channels);
                for (int frame = 0, p = 0; frame < mono.Length; frame++)
                {
                    long sum = 0;
                    for (int c = 0; c < channels; c++, p += 3)
                        sum += frames[p] | (frames[p + 1] << 8) | ((sbyte)frames[p + 2] << 16);
                    mono[frame] = (float)(sum * scale);
                }
                break;
            }
            case AudioSampleFormat.Int32:
            {
                var samples = MemoryMarshal.Cast<byte, int>(frames);
                double scale = 1.0 / ((double)int.MaxValue * channels);
                for (int frame = 0, i = 0; frame < mono.Length; frame++)
                {
                    long sum = 0;
                    for (int c = 0; c < channels; c++)
                        sum += samples[i++];
                    mono[frame] = (float)(sum * scale);
                }
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown sample format.");
        }
    }

    // Linear interpolation over window[0] = last sample of the previous call, window[1..n] = this call's samples.
    // Output k sits at input position _pos + (_phase + k·_stepNum) / _phaseDen and is produced once the sample after
    // it exists, so after N input samples in total exactly ceil(N · 16000 / inputRate) samples have been emitted.
    private byte[] Resample(ReadOnlySpan<float> window)
    {
        int n = window.Length - 1;
        long numerator = (long)n * _phaseDen - (long)_pos * _phaseDen - _phase;
        int count = numerator > 0 ? (int)((numerator + _stepNum - 1) / _stepNum) : 0;

        var result = new byte[count * sizeof(short)];
        var output = MemoryMarshal.Cast<byte, short>(result.AsSpan());
        float invDen = 1f / _phaseDen;
        int i = _pos;
        int phase = _phase;
        for (int k = 0; k < count; k++)
        {
            float a = window[i];
            output[k] = ToPcm16(a + (window[i + 1] - a) * (phase * invDen));
            i += _stepInt;
            phase += _stepFrac;
            if (phase >= _phaseDen)
            {
                phase -= _phaseDen;
                i++;
            }
        }

        _pos = i - n;
        _phase = phase;
        return result;
    }

    private static byte[] Encode(ReadOnlySpan<float> mono)
    {
        var result = new byte[mono.Length * sizeof(short)];
        var output = MemoryMarshal.Cast<byte, short>(result.AsSpan());
        for (int i = 0; i < mono.Length; i++)
            output[i] = ToPcm16(mono[i]);
        return result;
    }

    private static short ToPcm16(float value) => (short)MathF.Round(Math.Clamp(value, -1f, 1f) * short.MaxValue);

    private static int BytesPerSample(AudioSampleFormat format) => format switch
    {
        AudioSampleFormat.Float32 => 4,
        AudioSampleFormat.Int16 => 2,
        AudioSampleFormat.Int24 => 3,
        AudioSampleFormat.Int32 => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown sample format."),
    };

    private static int GreatestCommonDivisor(int a, int b)
    {
        while (b != 0)
            (a, b) = (b, a % b);
        return a;
    }

    /// <summary>RBJ-cookbook low-pass biquad, transposed direct form II, state kept across calls.</summary>
    private sealed class Biquad
    {
        // State this small is inaudible; flushing it keeps long silences out of slow denormal arithmetic.
        private const double FlushThreshold = 1e-15;

        private readonly double _b0;
        private readonly double _b1;
        private readonly double _b2;
        private readonly double _a1;
        private readonly double _a2;
        private double _z1;
        private double _z2;

        public Biquad(double sampleRate, double cutoffHz, double q)
        {
            double w0 = 2 * Math.PI * cutoffHz / sampleRate;
            double cos = Math.Cos(w0);
            double alpha = Math.Sin(w0) / (2 * q);
            double a0 = 1 + alpha;
            _b0 = (1 - cos) / 2 / a0;
            _b1 = (1 - cos) / a0;
            _b2 = _b0;
            _a1 = -2 * cos / a0;
            _a2 = (1 - alpha) / a0;
        }

        public void Process(Span<float> samples)
        {
            double b0 = _b0, b1 = _b1, b2 = _b2, a1 = _a1, a2 = _a2;
            double z1 = _z1, z2 = _z2;
            for (int i = 0; i < samples.Length; i++)
            {
                double x = samples[i];
                double y = b0 * x + z1;
                z1 = b1 * x - a1 * y + z2;
                z2 = b2 * x - a2 * y;
                samples[i] = (float)y;
            }

            _z1 = Math.Abs(z1) < FlushThreshold ? 0 : z1;
            _z2 = Math.Abs(z2) < FlushThreshold ? 0 : z2;
        }

        public void Reset()
        {
            _z1 = 0;
            _z2 = 0;
        }
    }
}
