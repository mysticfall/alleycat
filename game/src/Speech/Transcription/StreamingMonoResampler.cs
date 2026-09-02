namespace AlleyCat.Speech.Transcription;

/// <summary>
/// Stateful mono resampler to 16 kHz. It uses a 33-tap windowed-sinc low-pass filter (Hann window) before sampling,
/// rather than linear interpolation or decimation, to attenuate source content above the target Nyquist frequency.
/// </summary>
public sealed class StreamingMonoResampler
{
    /// <summary>The continuous 16 kHz capture sample rate consumed by local voice detection.</summary>
    public const int TargetSampleRate = 16000;

    private const int TapCount = 33;
    private const int HalfTaps = TapCount / 2;
    private readonly float[] _history = new float[TapCount * 2];
    private bool _flushed;

    /// <summary>Creates a streaming resampler for the supplied mono source rate.</summary>
    public StreamingMonoResampler(int sourceSampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceSampleRate);
        SourceSampleRate = sourceSampleRate;
        Ratio = (double)sourceSampleRate / TargetSampleRate;
        Cutoff = 0.45d * Math.Min(1d, (double)TargetSampleRate / sourceSampleRate);
    }

    /// <summary>Gets the source sample rate.</summary>
    public int SourceSampleRate
    {
        get;
    }

    /// <summary>Gets the number of source samples accepted.</summary>
    public long InputSampleCount
    {
        get;
        private set;
    }

    /// <summary>Gets the exact number of 16 kHz samples produced.</summary>
    public long OutputSampleCount
    {
        get;
        private set;
    }

    /// <summary>Gets the source-sample position represented by an output sample.</summary>
    public double GetSourceSamplePosition(long outputSampleIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(outputSampleIndex);
        return outputSampleIndex * Ratio;
    }

    /// <summary>Appends a chunk and writes every output sample whose future filter support is available.</summary>
    public int Process(ReadOnlySpan<float> source, Span<float> destination)
    {
        if (_flushed)
        {
            throw new InvalidOperationException("A flushed resampler must be reset before accepting more source samples.");
        }

        long projectedInputCount = checked(InputSampleCount + source.Length);
        long availableOutputCount = GetAvailableOutputCount(projectedInputCount, final: false);
        int required = checked((int)(availableOutputCount - OutputSampleCount));
        if (destination.Length < required)
        {
            throw new ArgumentException("Destination is too small for available resampled audio.", nameof(destination));
        }

        int written = 0;
        foreach (float sample in source)
        {
            _history[InputSampleCount % _history.Length] = float.IsFinite(sample) ? sample : 0f;
            InputSampleCount++;
            while (OutputSampleCount < GetAvailableOutputCount(final: false))
            {
                destination[written++] = Filter(OutputSampleCount++, final: false);
            }
        }

        return written;
    }

    /// <summary>Writes the delayed tail with zero extension and completes the exact sample clock for the input duration.</summary>
    public int Flush(Span<float> destination)
    {
        _flushed = true;
        long targetCount = GetAvailableOutputCount(final: true);
        int required = checked((int)(targetCount - OutputSampleCount));
        if (destination.Length < required)
        {
            throw new ArgumentException("Destination is too small for the resampler tail.", nameof(destination));
        }

        for (int index = 0; index < required; index++)
        {
            destination[index] = Filter(OutputSampleCount++, final: true);
        }

        return required;
    }

    /// <summary>Resets accumulated audio and sample-clock state.</summary>
    public void Reset()
    {
        Array.Clear(_history);
        InputSampleCount = 0;
        OutputSampleCount = 0;
        _flushed = false;
    }

    private long GetAvailableOutputCount(bool final) => GetAvailableOutputCount(InputSampleCount, final);

    private long GetAvailableOutputCount(long inputSampleCount, bool final)
    {
        if (inputSampleCount == 0)
        {
            return 0;
        }

        if (final)
        {
            return inputSampleCount * TargetSampleRate / SourceSampleRate;
        }

        long latestSupportedInput = inputSampleCount - 1 - HalfTaps;
        return latestSupportedInput < 0 ? 0 : Math.Max(0, (long)Math.Floor(latestSupportedInput / Ratio) + 1);
    }

    private float Filter(long outputIndex, bool final)
    {
        double position = outputIndex * Ratio;
        long centre = (long)Math.Floor(position);
        double sum = 0d;
        double weights = 0d;
        for (int tap = -HalfTaps; tap <= HalfTaps; tap++)
        {
            long sourceIndex = centre + tap;
            double offset = sourceIndex - position;
            double window = 0.5d + (0.5d * Math.Cos(Math.PI * offset / (HalfTaps + 1)));
            double scaled = 2d * Cutoff * offset;
            double sinc = Math.Abs(scaled) < 1e-12d ? 1d : Math.Sin(Math.PI * scaled) / (Math.PI * scaled);
            double weight = 2d * Cutoff * sinc * window;
            if (sourceIndex >= 0 && sourceIndex < InputSampleCount)
            {
                sum += GetInput(sourceIndex) * weight;
                weights += weight;
            }
            else if (!final && sourceIndex >= 0)
            {
                throw new InvalidOperationException("The resampler attempted to read unavailable future audio.");
            }
        }

        return weights == 0d ? 0f : (float)(sum / weights);
    }

    private float GetInput(long sampleIndex)
    {
        return InputSampleCount - sampleIndex <= _history.Length
            ? _history[sampleIndex % _history.Length]
            : throw new InvalidOperationException("Resampler history was insufficient for the requested output sample.");
    }

    private double Ratio
    {
        get;
    }

    private double Cutoff
    {
        get;
    }
}
