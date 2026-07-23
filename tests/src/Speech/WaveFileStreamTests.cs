using System.Buffers.Binary;
using AlleyCat.Speech.Transcription;
using Xunit;

namespace AlleyCat.Tests.Speech;

/// <summary>
/// Unit coverage for the seekable WAV view used by transcription uploads.
/// </summary>
public sealed class WaveFileStreamTests
{
    private static readonly byte[] _pcmData = [0x34, 0x12, 0x78, 0x56, 0xbc, 0x9a];
    private static readonly byte[] _expectedWave =
    [
        0x52, 0x49, 0x46, 0x46, 0x2a, 0x00, 0x00, 0x00,
        0x57, 0x41, 0x56, 0x45, 0x66, 0x6d, 0x74, 0x20,
        0x10, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00,
        0x80, 0x3e, 0x00, 0x00, 0x00, 0x7d, 0x00, 0x00,
        0x02, 0x00, 0x10, 0x00, 0x64, 0x61, 0x74, 0x61,
        0x06, 0x00, 0x00, 0x00, 0x34, 0x12, 0x78, 0x56,
        0xbc, 0x9a,
    ];

    /// <summary>
    /// The logical file must contain a canonical mono PCM16 header followed by the unchanged payload.
    /// </summary>
    [Fact]
    public void Read_MonoPCM16_ReturnsExactWaveFile()
    {
        using WaveFileStream stream = new(_pcmData, sampleRate: 16000, channelCount: 1);
        byte[] wave = new byte[stream.Length];

        int count = stream.Read(wave, 0, wave.Length);

        Assert.Equal(wave.Length, count);
        Assert.Equal(_expectedWave, wave);
        Assert.Equal("RIFF"u8.ToArray(), wave[..4]);
        Assert.Equal(36 + _pcmData.Length, BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(4, 4)));
        Assert.Equal("WAVEfmt "u8.ToArray(), wave[8..16]);
        Assert.Equal(16, BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(16, 4)));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(20, 2)));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(22, 2)));
        Assert.Equal(16000, BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(24, 4)));
        Assert.Equal(32000, BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(28, 4)));
        Assert.Equal(2, BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(32, 2)));
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(34, 2)));
        Assert.Equal("data"u8.ToArray(), wave[36..40]);
        Assert.Equal(_pcmData.Length, BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(40, 4)));
        Assert.Equal(_pcmData, wave[WaveFileStream.HeaderLength..]);
    }

    /// <summary>
    /// Span reads must work independently in the header, payload, and across their boundary.
    /// </summary>
    [Theory]
    [InlineData(0, 4, new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' })]
    [InlineData(44, 4, new byte[] { 0x34, 0x12, 0x78, 0x56 })]
    [InlineData(42, 4, new byte[] { 0x00, 0x00, 0x34, 0x12 })]
    public void Read_SegmentPosition_ReturnsExpectedBytes(int position, int count, byte[] expected)
    {
        using WaveFileStream stream = new(_pcmData, sampleRate: 16000, channelCount: 1);
        stream.Position = position;
        Span<byte> buffer = stackalloc byte[count];

        int bytesRead = stream.Read(buffer);

        Assert.Equal(count, bytesRead);
        Assert.Equal(expected, buffer.ToArray());
    }

    /// <summary>
    /// Arbitrary array read sizes must concatenate to the independently specified logical file without skipping bytes.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(43)]
    [InlineData(44)]
    [InlineData(45)]
    [InlineData(50)]
    [InlineData(51)]
    [InlineData(97)]
    public void Read_ArbitraryArrayBufferSizes_ReturnsCompleteFile(int bufferSize)
    {
        using WaveFileStream stream = new(_pcmData, 16000, 1);
        List<byte> actual = [];
        byte[] buffer = new byte[bufferSize];

        int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
        {
            actual.AddRange(buffer.AsSpan(0, count).ToArray());
        }

        Assert.Equal(_expectedWave, actual);
    }

    /// <summary>
    /// Invalid legacy read arguments must fail without consuming any bytes.
    /// </summary>
    [Fact]
    public void Read_InvalidArrayArguments_ThrowsWithoutAdvancing()
    {
        using WaveFileStream stream = new(_pcmData, 16000, 1);
        byte[] buffer = new byte[4];

        _ = Assert.Throws<ArgumentNullException>(() => stream.Read(null!, 0, 0));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => stream.Read(buffer, -1, 1));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => stream.Read(buffer, 0, -1));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => stream.Read(buffer, 3, 2));

        Assert.Equal(0, stream.Position);
    }

    /// <summary>
    /// Begin, current, end, and Position seeks must all resolve against the logical WAV length.
    /// </summary>
    [Fact]
    public void Seek_AllOriginsAndPositionSetter_UpdatePosition()
    {
        using WaveFileStream stream = new(_pcmData, 16000, 1);

        Assert.Equal(10, stream.Seek(10, SeekOrigin.Begin));
        Assert.Equal(15, stream.Seek(5, SeekOrigin.Current));
        Assert.Equal(stream.Length - 2, stream.Seek(-2, SeekOrigin.End));
        stream.Position = 44;

        Assert.Equal(44, stream.Position);
        Assert.Equal(0x34, stream.ReadByte());
    }

    /// <summary>
    /// Seeking before zero or overflowing a seek calculation must fail without changing position.
    /// </summary>
    [Fact]
    public void Seek_InvalidPosition_ThrowsIOException()
    {
        using WaveFileStream stream = new(_pcmData, 16000, 1);
        stream.Position = 5;

        _ = Assert.Throws<IOException>(() => stream.Seek(-6, SeekOrigin.Current));
        _ = Assert.Throws<IOException>(() => stream.Seek(long.MaxValue, SeekOrigin.End));
        Assert.Equal(5, stream.Position);
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => stream.Seek(0, (SeekOrigin)99));
    }

    /// <summary>
    /// EOF reads must return zero and rewinding must permit an exact replay.
    /// </summary>
    [Fact]
    public void Read_EndOfFileThenReplay_ReturnsExactBytesAgain()
    {
        using WaveFileStream stream = new(_pcmData, 16000, 1);
        byte[] first = new byte[stream.Length];
        Assert.Equal(first.Length, stream.Read(first));
        Assert.Equal(0, stream.Read(first));
        Span<byte> empty = [];
        Assert.Equal(0, stream.Read(empty));

        stream.Position = 0;
        byte[] second = new byte[stream.Length];

        Assert.Equal(second.Length, stream.Read(second));
        Assert.Equal(first, second);
    }

    /// <summary>
    /// Both modern memory and legacy array asynchronous reads must preserve stream semantics.
    /// </summary>
    [Fact]
    public async Task ReadAsync_MemoryAndArray_ReturnExpectedBytes()
    {
        using WaveFileStream stream = new(_pcmData, 16000, 1);
        stream.Position = 42;
        Memory<byte> memory = new byte[4];

        int memoryCount = await stream.ReadAsync(memory);
        stream.Position = 44;
        byte[] array = new byte[2];
        int arrayCount = await stream.ReadAsync(array, 0, array.Length, CancellationToken.None);

        Assert.Equal(4, memoryCount);
        Assert.Equal([0x00, 0x00, 0x34, 0x12], memory.ToArray());
        Assert.Equal(2, arrayCount);
        Assert.Equal([0x34, 0x12], array);
    }

    /// <summary>
    /// Cancelled asynchronous reads must not consume stream data.
    /// </summary>
    [Fact]
    public async Task ReadAsync_Cancelled_ThrowsWithoutAdvancing()
    {
        using WaveFileStream stream = new(_pcmData, 16000, 1);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

#pragma warning disable CA2022 // A partial read cannot occur because the operation is already cancelled.
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await stream.ReadAsync(new byte[1], cancellation.Token));
#pragma warning restore CA2022

        Assert.Equal(0, stream.Position);
    }

    /// <summary>
    /// The stream advertises and enforces read-only behaviour while Flush remains a no-op.
    /// </summary>
    [Fact]
    public void WriteMembers_ReadOnlyStream_RejectMutation()
    {
        using WaveFileStream stream = new(_pcmData, 16000, 1);

        Assert.True(stream.CanRead);
        Assert.True(stream.CanSeek);
        Assert.False(stream.CanWrite);
        stream.Flush();
        _ = Assert.Throws<NotSupportedException>(() => stream.Write([1], 0, 1));
        _ = Assert.Throws<NotSupportedException>(() => stream.Write([1]));
        _ = Assert.Throws<NotSupportedException>(() => stream.SetLength(1));
    }

    /// <summary>
    /// Disposing repeatedly must be safe and subsequent readable members must reject use.
    /// </summary>
    [Fact]
    public void Dispose_Repeatedly_ClosesReadableMembers()
    {
        WaveFileStream stream = new(_pcmData, 16000, 1);

        stream.Dispose();
        stream.Dispose();

        Assert.False(stream.CanRead);
        Assert.False(stream.CanSeek);
        Assert.False(stream.CanWrite);
        _ = Assert.Throws<ObjectDisposedException>(() => _ = stream.Length);
        _ = Assert.Throws<ObjectDisposedException>(() => _ = stream.Position);
        _ = Assert.Throws<ObjectDisposedException>(() => stream.Position = 0);
        _ = Assert.Throws<ObjectDisposedException>(() => stream.Seek(0, SeekOrigin.Begin));
        _ = Assert.Throws<ObjectDisposedException>(() => stream.Read(new byte[1]));
        _ = Assert.Throws<ObjectDisposedException>(stream.Flush);
    }

    /// <summary>
    /// Asynchronous reads after disposal must fail rather than report an empty stream.
    /// </summary>
    [Fact]
    public async Task ReadAsync_AfterDisposal_ThrowsObjectDisposedException()
    {
        WaveFileStream stream = new(_pcmData, 16000, 1);
        stream.Dispose();

#pragma warning disable CA2022 // No partial read can occur because the stream is already disposed.
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await stream.ReadAsync(new byte[1], 0, 1, CancellationToken.None));
#pragma warning restore CA2022
    }

    /// <summary>
    /// WAV metadata and PCM payloads must be valid, aligned, and representable by RIFF fields.
    /// </summary>
    [Fact]
    public void Constructor_InvalidMetadata_Throws()
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new WaveFileStream(_pcmData, 0, 1));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new WaveFileStream(_pcmData, 16000, 0));
        _ = Assert.Throws<ArgumentException>(() => new WaveFileStream(new byte[3], 16000, 1));
        _ = Assert.Throws<ArgumentException>(() => new WaveFileStream(new byte[6], 16000, 2));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new WaveFileStream(new byte[4], int.MaxValue, 2));
    }
}
