using System.Buffers.Binary;

namespace AlleyCat.Speech.Transcription;

/// <summary>
/// Presents immutable PCM16 memory as a canonical WAV file without copying its payload.
/// </summary>
internal sealed class WaveFileStream : Stream
{
    internal const int HeaderLength = 44;

    private readonly byte[] _header = new byte[HeaderLength];
    private long _position;
    private bool _disposed;

    public WaveFileStream(ReadOnlyMemory<byte> pcmData, int sampleRate, short channelCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channelCount);

        int blockAlign = checked(channelCount * sizeof(short));
        if (pcmData.Length % blockAlign != 0)
        {
            throw new ArgumentException(
                "PCM16 data must contain complete frames for the specified channel count.",
                nameof(pcmData));
        }

        ulong byteRateValue = (ulong)sampleRate * (uint)blockAlign;
        if (byteRateValue > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleRate),
                sampleRate,
                "The WAV byte rate exceeds the RIFF field limit.");
        }

        long riffSize = 36L + pcmData.Length;
        if (riffSize > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(pcmData), "The PCM payload exceeds the RIFF size limit.");
        }

        PCMData = pcmData;
        WriteHeader(_header, pcmData.Length, sampleRate, channelCount, (ushort)blockAlign, (uint)byteRateValue);
    }

    public override bool CanRead => !_disposed;

    public override bool CanSeek => !_disposed;

    public override bool CanWrite => false;

    internal ReadOnlyMemory<byte> PCMData
    {
        get;
    }

    public override long Length
    {
        get
        {
            ThrowIfDisposed();
            return HeaderLength + (long)PCMData.Length;
        }
    }

    public override long Position
    {
        get
        {
            ThrowIfDisposed();
            return _position;
        }
        set => Seek(value, SeekOrigin.Begin);
    }

    public override void Flush()
        => ThrowIfDisposed();

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, buffer.Length - count);

        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();
        if (buffer.IsEmpty || _position >= Length)
        {
            return 0;
        }

        int totalRead = 0;
        if (_position < HeaderLength)
        {
            int headerOffset = (int)_position;
            int headerCount = Math.Min(buffer.Length, HeaderLength - headerOffset);
            _header.AsSpan(headerOffset, headerCount).CopyTo(buffer);
            _position += headerCount;
            totalRead += headerCount;
            buffer = buffer[headerCount..];
        }

        if (!buffer.IsEmpty && _position >= HeaderLength)
        {
            int pcmOffset = (int)(_position - HeaderLength);
            int pcmCount = Math.Min(buffer.Length, PCMData.Length - pcmOffset);
            PCMData.Span.Slice(pcmOffset, pcmCount).CopyTo(buffer);
            _position += pcmCount;
            totalRead += pcmCount;
        }

        return totalRead;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<int>(cancellationToken);
        }

        try
        {
            return Task.FromResult(Read(buffer, offset, count));
        }
        catch (Exception ex)
        {
            return Task.FromException<int>(ex);
        }
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => cancellationToken.IsCancellationRequested
            ? ValueTask.FromCanceled<int>(cancellationToken)
            : ValueTask.FromResult(Read(buffer.Span));

    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfDisposed();

        long originPosition = origin switch
        {
            SeekOrigin.Begin => 0,
            SeekOrigin.Current => _position,
            SeekOrigin.End => Length,
            _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unknown seek origin."),
        };

        long newPosition;
        try
        {
            newPosition = checked(originPosition + offset);
        }
        catch (OverflowException ex)
        {
            throw new IOException("Attempted to seek outside the stream.", ex);
        }

        if (newPosition < 0)
        {
            throw new IOException("Attempted to seek before the beginning of the stream.");
        }

        _position = newPosition;
        return _position;
    }

    public override void SetLength(long value)
        => throw new NotSupportedException("The WAV stream is read-only.");

    public override void Write(byte[] buffer, int offset, int count)
        => throw new NotSupportedException("The WAV stream is read-only.");

    public override void Write(ReadOnlySpan<byte> buffer)
        => throw new NotSupportedException("The WAV stream is read-only.");

    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        base.Dispose(disposing);
    }

    private static void WriteHeader(
        Span<byte> header,
        int dataLength,
        int sampleRate,
        short channelCount,
        ushort blockAlign,
        uint byteRate)
    {
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], checked((uint)(36L + dataLength)));
        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], (ushort)channelCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], byteRate);
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], 16);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], (uint)dataLength);
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);
}
