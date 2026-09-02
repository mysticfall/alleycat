namespace AlleyCat.Speech.Transcription;

/// <summary>A bounded mono-float ring buffer for audio retained before qualified onset.</summary>
public sealed class MonoFloatPreRollBuffer
{
    private readonly float[] _buffer;
    private int _start;

    /// <summary>Creates a bounded pre-roll buffer.</summary>
    public MonoFloatPreRollBuffer(int capacitySamples)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacitySamples);
        _buffer = new float[capacitySamples];
    }

    /// <summary>Gets the maximum retained sample count.</summary>
    public int Capacity => _buffer.Length;

    /// <summary>Gets the current retained sample count.</summary>
    public int Count
    {
        get;
        private set;
    }

    /// <summary>Appends samples, overwriting the oldest samples when full.</summary>
    public void Append(ReadOnlySpan<float> samples)
    {
        foreach (float sample in samples)
        {
            int write = (_start + Count) % Capacity;
            if (Count == Capacity)
            {
                _buffer[write] = sample;
                _start = (_start + 1) % Capacity;
            }
            else
            {
                _buffer[write] = sample;
                Count++;
            }
        }
    }

    /// <summary>Copies and removes retained samples in exact oldest-to-newest order.</summary>
    public int DrainTo(Span<float> destination)
    {
        int count = Math.Min(Count, destination.Length);
        for (int index = 0; index < count; index++)
        {
            destination[index] = _buffer[(_start + index) % Capacity];
        }

        _start = (_start + count) % Capacity;
        Count -= count;
        return count;
    }

    /// <summary>Clears retained samples without allocating.</summary>
    public void Clear()
    {
        _start = 0;
        Count = 0;
    }
}
