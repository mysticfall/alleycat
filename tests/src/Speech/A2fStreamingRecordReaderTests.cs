using System.Globalization;
using System.Text;
using System.Text.Json;
using AlleyCat.Speech.LipSync;
using AlleyCat.Vision;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlleyCat.Tests.Speech;

/// <summary>
/// Unit coverage for the Audio2Face streaming NDJSON record reader: record-type handling, validation
/// failures, per-frame conversion, and the inter-record idle timeout, exercised against in-memory
/// streams without a live HTTP server.
/// </summary>
public sealed class A2fStreamingRecordReaderTests
{
    private static readonly A2fEyeTranslationSettings _disabledEyeTranslation = new(
        TranslateEyeRotations: false,
        EyeRotationScale: 2f,
        EyeSmoothingAlpha: 0.4f,
        InvertHorizontal: false,
        InvertVertical: false);

    /// <summary>
    /// A consumer that has ended playback must cause the reader to stop before it validates any late
    /// frame, so stale records cannot be reported as an artificial index or malformed-stream fault.
    /// </summary>
    [Fact]
    public async Task ReadAsync_WhenConsumerIsClosed_IgnoresLateFrameWithoutFaulting()
    {
        StreamingFrameBuffer buffer = new(startupBufferSeconds: 0.1f);
        buffer.SetMetadata(30f, ["jawOpen"]);
        buffer.Append([0.5f]);
        buffer.CloseConsumer();

        await ReadAsync(
            BuildNdjson(
                FrameLine(99, [0.9f]),
                "{malformed"),
            buffer);

        Assert.True(buffer.IsConsumerClosed);
        Assert.False(buffer.IsCompleted);
        Assert.False(buffer.IsFaulted);
        Assert.Equal(1, buffer.FrameCount);
    }

    /// <summary>
    /// A well-formed metadata/frame/complete sequence must populate metadata, frames, and completion in
    /// the buffer.
    /// </summary>
    [Fact]
    public async Task ReadAsync_WithMetadataFrameCompleteSequence_PopulatesBufferAndCompletes()
    {
        StreamingFrameBuffer buffer = new(startupBufferSeconds: 1f);

        await ReadAsync(
            BuildNdjson(
                MetadataLine(60f, ["jawOpen", "mouthPucker"]),
                FrameLine(0, [0.1f, 0.2f]),
                FrameLine(1, [0.6f, 0.7f]),
                FrameLine(2, [0.9f, 0.4f]),
                CompleteLine(3)),
            buffer);

        Assert.True(buffer.IsCompleted);
        Assert.Equal(3, buffer.DeclaredFrameCount);
        Assert.True(buffer.TryGetMetadata(out float outputFps, out IReadOnlyList<string> blendshapeNames));
        Assert.Equal(60f, outputFps);
        Assert.Equal(["jawOpen", "mouthPucker"], blendshapeNames);
        Assert.True(buffer.TryGetFrame(0, out float[] first));
        Assert.Equal([0.1f, 0.2f], first);
        Assert.True(buffer.TryGetFrame(2, out float[] last));
        Assert.Equal([0.9f, 0.4f], last);
    }

    /// <summary>
    /// Eye-controlled blendshape channels must be stripped from the retained names even when
    /// eye-rotation translation is enabled but the eye channel set is incomplete.
    /// </summary>
    [Fact]
    public async Task ReadAsync_WithIncompleteEyeChannelsAndTranslation_StripsEyeNamesAndWarns()
    {
        SpyLogger logger = new();
        StreamingFrameBuffer buffer = new(startupBufferSeconds: 1f);
        A2fEyeTranslationSettings translation = _disabledEyeTranslation with
        {
            TranslateEyeRotations = true,
        };

        await ReadAsync(
            BuildNdjson(
                MetadataLine(30f, ["jawOpen", "eyeLookUpLeft"]),
                FrameLine(0, [0.5f, 1f]),
                CompleteLine(1)),
            buffer,
            logger,
            translation);

        Assert.True(buffer.TryGetMetadata(out _, out IReadOnlyList<string> blendshapeNames));
        Assert.Equal(["jawOpen"], blendshapeNames);
        Assert.True(buffer.TryGetFrame(0, out float[] frame));
        Assert.Equal([0.5f], frame);
        Assert.Contains(
            logger.Entries,
            entry => entry.Level == LogLevel.Warning
                && entry.Message.Contains("eyeLook blendshape channels are missing", StringComparison.Ordinal));
    }

    /// <summary>
    /// With the full ARKit eye channel set, eye-rotation translation runs per frame but the eyeLook
    /// channels it writes are stripped, so retained frames carry only the non-eye weights.
    /// </summary>
    [Fact]
    public async Task ReadAsync_WithFullEyeChannelSet_RetainsOnlyNonEyeWeights()
    {
        List<string> names = ["jawOpen", .. EyesAnimationTreePaths.EyeBlendShapeNames];
        StreamingFrameBuffer buffer = new(startupBufferSeconds: 1f);
        A2fEyeTranslationSettings translation = _disabledEyeTranslation with
        {
            TranslateEyeRotations = true,
        };

        await ReadAsync(
            BuildNdjson(
                MetadataLine(60f, names),
                FrameLine(0, CreateWeights(names.Count, jaw: 0.25f), eyeRotation: [0.1f, 0.2f, 0.3f, 0.1f, 0.2f, 0.3f]),
                FrameLine(1, CreateWeights(names.Count, jaw: 0.75f), eyeRotation: [0.2f, 0.1f, 0.0f, 0.2f, 0.1f, 0.0f]),
                CompleteLine(2)),
            buffer,
            settings: translation);

        Assert.True(buffer.TryGetMetadata(out _, out IReadOnlyList<string> retainedNames));
        Assert.Equal(["jawOpen"], retainedNames);
        Assert.True(buffer.TryGetFrame(0, out float[] first));
        Assert.Equal([0.25f], first);
        Assert.True(buffer.TryGetFrame(1, out float[] second));
        Assert.Equal([0.75f], second);
    }

    /// <summary>
    /// Unknown record types must be skipped with one warning per distinct type, without disturbing the
    /// surrounding frames.
    /// </summary>
    [Fact]
    public async Task ReadAsync_WithUnknownRecordType_SkipsAndWarnsOncePerType()
    {
        SpyLogger logger = new();
        StreamingFrameBuffer buffer = new(startupBufferSeconds: 1f);

        await ReadAsync(
            BuildNdjson(
                MetadataLine(30f, ["jawOpen"]),
                UnknownRecordLine("progress", percent: 50),
                FrameLine(0, [0.4f]),
                UnknownRecordLine("progress", percent: 100),
                UnknownRecordLine("heartbeat"),
                CompleteLine(1)),
            buffer,
            logger);

        Assert.True(buffer.IsCompleted);
        Assert.Equal(1, buffer.FrameCount);

        IReadOnlyList<string> warningMessages =
        [
            .. logger.Entries
                .Where(entry => entry.Level == LogLevel.Warning)
                .Select(entry => entry.Message),
        ];
        Assert.Equal(2, warningMessages.Count);
        Assert.Contains(warningMessages, message => message.Contains("'progress'", StringComparison.Ordinal));
        Assert.Contains(warningMessages, message => message.Contains("'heartbeat'", StringComparison.Ordinal));
    }

    /// <summary>
    /// Blank separator lines must be skipped without failing the stream.
    /// </summary>
    [Fact]
    public async Task ReadAsync_WithBlankLines_SkipsThem()
    {
        StreamingFrameBuffer buffer = new(startupBufferSeconds: 1f);

        await ReadAsync(
            "\n"
            + MetadataLine(30f, ["jawOpen"]) + "\n\n"
            + FrameLine(0, [0.5f]) + "\n"
            + CompleteLine(1) + "\n",
            buffer);

        Assert.True(buffer.IsCompleted);
        Assert.Equal(1, buffer.FrameCount);
    }

    /// <summary>
    /// A malformed JSON line must fail the stream with the offending line in the error.
    /// </summary>
    [Fact]
    public async Task ReadAsync_WithMalformedLine_ThrowsParseError()
    {
        StreamingFrameBuffer buffer = new(startupBufferSeconds: 1f);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ReadAsync(MetadataLine(30f, ["jawOpen"]) + "\n{not json at all\n", buffer));

        Assert.Contains("failed to parse Audio2Face streaming record", ex.Message, StringComparison.Ordinal);
        Assert.Contains("{not json at all", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A stream that ends without the complete record must fail instead of completing the buffer.
    /// </summary>
    [Fact]
    public async Task ReadAsync_WithEofBeforeCompleteRecord_Throws()
    {
        StreamingFrameBuffer buffer = new(startupBufferSeconds: 1f);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ReadAsync(MetadataLine(30f, ["jawOpen"]) + "\n" + FrameLine(0, [0.5f]) + "\n", buffer));

        Assert.Contains("ended without a complete record", ex.Message, StringComparison.Ordinal);
        Assert.False(buffer.IsCompleted);
    }

    /// <summary>
    /// A complete record declaring zero frames must fail: playback requires at least one frame.
    /// </summary>
    [Fact]
    public async Task ReadAsync_WithZeroFrameComplete_Throws()
    {
        StreamingFrameBuffer buffer = new(startupBufferSeconds: 1f);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ReadAsync(MetadataLine(30f, ["jawOpen"]) + "\n" + CompleteLine(0) + "\n", buffer));

        Assert.Contains("produced zero frames", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The declared frame count must match the received frames.
    /// </summary>
    [Fact]
    public async Task ReadAsync_WithDeclaredCountMismatch_Throws()
    {
        StreamingFrameBuffer buffer = new(startupBufferSeconds: 1f);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ReadAsync(
                MetadataLine(30f, ["jawOpen"]) + "\n" + FrameLine(0, [0.5f]) + "\n" + CompleteLine(3) + "\n",
                buffer));

        Assert.Contains("declared 3 frames but 1 were received", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Frame indexes must arrive strictly sequentially from zero.
    /// </summary>
    [Fact]
    public async Task ReadAsync_WithNonSequentialFrameIndex_Throws()
    {
        StreamingFrameBuffer buffer = new(startupBufferSeconds: 1f);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ReadAsync(
                MetadataLine(30f, ["jawOpen"]) + "\n" + FrameLine(0, [0.5f]) + "\n" + FrameLine(2, [0.7f]) + "\n",
                buffer));

        Assert.Contains("frame index 2 does not match the expected sequential index 1", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Frame records must not arrive before the metadata record.
    /// </summary>
    [Fact]
    public async Task ReadAsync_WithFrameBeforeMetadata_Throws()
    {
        StreamingFrameBuffer buffer = new(startupBufferSeconds: 1f);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ReadAsync(FrameLine(0, [0.5f]) + "\n", buffer));

        Assert.Contains("frame record arrived before metadata", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every record must be a JSON object carrying a string type field.
    /// </summary>
    [Fact]
    public async Task ReadAsync_WithNonObjectRecordOrMissingType_Throws()
    {
        StreamingFrameBuffer nonObjectBuffer = new(startupBufferSeconds: 1f);
        InvalidOperationException nonObjectError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ReadAsync("[1,2,3]\n", nonObjectBuffer));
        Assert.Contains("was not a JSON object", nonObjectError.Message, StringComparison.Ordinal);

        StreamingFrameBuffer missingTypeBuffer = new(startupBufferSeconds: 1f);
        InvalidOperationException missingTypeError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ReadAsync(MissingTypeLine() + "\n", missingTypeBuffer));
        Assert.Contains("missing its type field", missingTypeError.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Frame weights must match the metadata-declared channel count.
    /// </summary>
    [Fact]
    public async Task ReadAsync_WithWeightsChannelCountMismatch_Throws()
    {
        StreamingFrameBuffer buffer = new(startupBufferSeconds: 1f);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ReadAsync(
                MetadataLine(30f, ["jawOpen", "mouthPucker"]) + "\n" + FrameLine(0, [0.5f]) + "\n",
                buffer));

        Assert.Contains("frame 0 has 1 channels, expected 2", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Lines must reassemble correctly no matter how the transport chunks them, including one byte at a
    /// time and lines larger than the reader's chunk buffer.
    /// </summary>
    [Fact]
    public async Task NdjsonLineReader_WithArbitraryChunking_ReassemblesLines()
    {
        byte[] payload = Encoding.UTF8.GetBytes(
            MetadataLine(30f, ["jawOpen"]) + "\n" + FrameLine(0, [0.5f]) + "\r\n" + "trailing-no-newline");

        List<string> lines = [];
        using SegmentedStream stream = new(SplitByteByByte(payload));
        A2fStreamingRecordReader.NdjsonLineReader reader = new(stream, TimeSpan.FromSeconds(5));

        while (await reader.ReadLineAsync(CancellationToken.None) is { } line)
        {
            lines.Add(line);
        }

        Assert.Equal(
            [MetadataLine(30f, ["jawOpen"]), FrameLine(0, [0.5f]), "trailing-no-newline"],
            lines);
    }

    /// <summary>
    /// Lines longer than the reader's chunk buffer must be reassembled intact through the overflow path.
    /// </summary>
    [Fact]
    public async Task NdjsonLineReader_WithLineLongerThanChunkBuffer_ReturnsItIntact()
    {
        string longLine = new('x', 20_000);
        byte[] payload = Encoding.UTF8.GetBytes(longLine + "\n");

        using SegmentedStream stream = new(
        [
            payload[..10_000],
            payload[10_000..],
        ]);
        A2fStreamingRecordReader.NdjsonLineReader reader = new(stream, TimeSpan.FromSeconds(5));

        Assert.Equal(longLine, await reader.ReadLineAsync(CancellationToken.None));
        Assert.Null(await reader.ReadLineAsync(CancellationToken.None));
    }

    /// <summary>
    /// The inter-record idle timeout must fail a stalled stream with a timeout error rather than
    /// hanging, while the outer cancellation token stays uncancelled.
    /// </summary>
    [Fact]
    public async Task NdjsonLineReader_WhenNoDataArrivesWithinIdleTimeout_ThrowsTimeoutException()
    {
        byte[] firstLine = Encoding.UTF8.GetBytes(MetadataLine(30f, ["jawOpen"]) + "\n");
        using SegmentedStream stream = new([firstLine])
        {
            BlockForeverAfterChunks = true,
        };
        A2fStreamingRecordReader.NdjsonLineReader reader = new(stream, TimeSpan.FromMilliseconds(100));

        Assert.Equal(MetadataLine(30f, ["jawOpen"]), await reader.ReadLineAsync(CancellationToken.None));

        TimeoutException ex = await Assert.ThrowsAsync<TimeoutException>(
            () => reader.ReadLineAsync(CancellationToken.None));

        Assert.Contains("received no data for", ex.Message, StringComparison.Ordinal);
    }

    private static Task ReadAsync(
        string ndjson,
        StreamingFrameBuffer buffer,
        ILogger? logger = null,
        A2fEyeTranslationSettings? settings = null)
    {
        A2fStreamingRecordReader reader = new(
            TimeSpan.FromSeconds(10),
            settings ?? _disabledEyeTranslation,
            logger ?? NullLogger.Instance);

        using MemoryStream stream = new(Encoding.UTF8.GetBytes(ndjson));
        return reader.ReadAsync(stream, buffer, CancellationToken.None);
    }

    private static string BuildNdjson(params string[] lines)
        => string.Join('\n', lines);

    private static string MetadataLine(float fps, IReadOnlyList<string> names)
        => "{\"type\":\"metadata\",\"fps\":" + fps.ToString(CultureInfo.InvariantCulture)
            + ",\"blendshape_names\":[" + string.Join(",", names.Select(name => $"\"{name}\"")) + "]}";

    private static string FrameLine(int index, IReadOnlyList<float> weights, IReadOnlyList<float>? eyeRotation = null)
    {
        string weightsCsv = string.Join(",", weights.Select(weight => weight.ToString(CultureInfo.InvariantCulture)));
        string eyeRotationSuffix = eyeRotation is null
            ? string.Empty
            : ",\"eye_rotation\":[" + string.Join(",", eyeRotation.Select(value => value.ToString(CultureInfo.InvariantCulture))) + "]";

        return "{\"type\":\"frame\",\"index\":" + index.ToString(CultureInfo.InvariantCulture)
            + ",\"timestamp\":" + (index * 267).ToString(CultureInfo.InvariantCulture)
            + ",\"weights\":[" + weightsCsv + "]" + eyeRotationSuffix + "}";
    }

    private static string CompleteLine(int frameCount)
        => "{\"type\":\"complete\",\"frame_count\":" + frameCount.ToString(CultureInfo.InvariantCulture) + "}";

    private static string UnknownRecordLine(string type, int? percent = null)
    {
        Dictionary<string, object> record = new()
        {
            ["type"] = type,
        };

        if (percent is { } value)
        {
            record.Add("percent", value);
        }

        return JsonSerializer.Serialize(record);
    }

    private static string MissingTypeLine()
        => JsonSerializer.Serialize(
            new Dictionary<string, float>
            {
                ["fps"] = 30f,
            });

    private static float[] CreateWeights(int channelCount, float jaw)
    {
        float[] weights = new float[channelCount];
        Array.Fill(weights, 0.1f);
        weights[0] = jaw;
        return weights;
    }

    private static byte[][] SplitByteByByte(byte[] payload)
        => [.. payload.Select(value => new[] { value })];

    /// <summary>
    /// A readable stream serving the supplied chunks sequentially, splitting chunks larger than the
    /// caller's buffer across reads, and optionally blocking forever once exhausted to simulate a
    /// stalled download.
    /// </summary>
    private sealed class SegmentedStream(byte[][] chunks) : Stream
    {
        private int _chunkIndex;
        private int _chunkOffset;

        public bool BlockForeverAfterChunks
        {
            get;
            init;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await Task.Yield();
            while (_chunkIndex < chunks.Length && _chunkOffset >= chunks[_chunkIndex].Length)
            {
                _chunkIndex++;
                _chunkOffset = 0;
            }

            if (_chunkIndex >= chunks.Length)
            {
                if (BlockForeverAfterChunks)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                return 0;
            }

            byte[] chunk = chunks[_chunkIndex];
            int bytesToCopy = Math.Min(count, chunk.Length - _chunkOffset);
            Array.Copy(chunk, _chunkOffset, buffer, offset, bytesToCopy);
            _chunkOffset += bytesToCopy;
            return bytesToCopy;
        }
    }

    /// <summary>
    /// Captures every logged entry so warning behaviour can be asserted without the Godot logging
    /// infrastructure.
    /// </summary>
    private sealed class SpyLogger : ILogger
    {
        private readonly Lock _lock = new();
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (_lock)
                {
                    return [.. _entries];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_lock)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
