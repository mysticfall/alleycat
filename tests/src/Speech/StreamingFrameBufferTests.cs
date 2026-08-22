using AlleyCat.Speech.LipSync;
using Xunit;

namespace AlleyCat.Tests.Speech;

/// <summary>
/// Unit coverage for the thread-safe streaming frame buffer shared between the inference reader thread
/// and main-thread playback.
/// </summary>
public sealed class StreamingFrameBufferTests
{
    /// <summary>
    /// Metadata must arrive before frames; appending earlier must fail so stream ordering violations
    /// surface immediately.
    /// </summary>
    [Fact]
    public void Append_BeforeMetadata_ThrowsStreamOrderingError()
    {
        StreamingFrameBuffer buffer = new(startupBufferSeconds: 0.1f);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => buffer.Append([0.5f]));

        Assert.Contains("arrived before metadata", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Metadata must declare a positive frame rate and at least one blendshape channel.
    /// </summary>
    [Fact]
    public void SetMetadata_WithInvalidFpsOrEmptyNames_Throws()
    {
        StreamingFrameBuffer buffer = new(startupBufferSeconds: 0.1f);

        _ = Assert.Throws<InvalidOperationException>(() => buffer.SetMetadata(0f, ["jawOpen"]));
        _ = Assert.Throws<InvalidOperationException>(() => buffer.SetMetadata(-30f, ["jawOpen"]));
        _ = Assert.Throws<InvalidOperationException>(() => buffer.SetMetadata(30f, []));
    }

    /// <summary>
    /// Metadata must arrive exactly once per stream.
    /// </summary>
    [Fact]
    public void SetMetadata_WhenCalledTwice_Throws()
    {
        StreamingFrameBuffer buffer = new(startupBufferSeconds: 0.1f);
        buffer.SetMetadata(30f, ["jawOpen"]);

        _ = Assert.Throws<InvalidOperationException>(() => buffer.SetMetadata(30f, ["jawOpen"]));
    }

    /// <summary>
    /// Frames append in order and read back by index, including the boundary indices.
    /// </summary>
    [Fact]
    public void Append_ThenTryGetFrame_ReturnsFramesByIndex()
    {
        StreamingFrameBuffer buffer = new(startupBufferSeconds: 0.1f);
        buffer.SetMetadata(30f, ["jawOpen"]);

        buffer.Append([0.1f]);
        buffer.Append([0.6f]);
        buffer.Append([0.9f]);

        Assert.Equal(3, buffer.FrameCount);
        Assert.True(buffer.TryGetFrame(0, out float[] first));
        Assert.Equal([0.1f], first);
        Assert.True(buffer.TryGetFrame(2, out float[] last));
        Assert.Equal([0.9f], last);
        Assert.False(buffer.TryGetFrame(3, out float[] beyondEnd));
        Assert.Empty(beyondEnd);
        Assert.False(buffer.TryGetFrame(-1, out float[] belowZero));
        Assert.Empty(belowZero);
    }

    /// <summary>
    /// Every appended frame must match the first frame's channel count.
    /// </summary>
    [Fact]
    public void Append_WithInconsistentChannelCount_Throws()
    {
        StreamingFrameBuffer buffer = new(startupBufferSeconds: 0.1f);
        buffer.SetMetadata(30f, ["jawOpen", "mouthPucker"]);
        buffer.Append([0.1f, 0.2f]);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => buffer.Append([0.5f]));

        Assert.Contains("channels, expected", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Frames must not arrive after the complete record.
    /// </summary>
    [Fact]
    public void Append_AfterMarkComplete_Throws()
    {
        StreamingFrameBuffer buffer = new(startupBufferSeconds: 0.1f);
        buffer.SetMetadata(30f, ["jawOpen"]);
        buffer.Append([0.1f]);
        buffer.MarkComplete(1);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => buffer.Append([0.5f]));

        Assert.Contains("after the complete record", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Completion records the declared count and flips the completion flag; metadata stays readable.
    /// </summary>
    [Fact]
    public void MarkComplete_RecordsDeclaredFrameCountAndCompletes()
    {
        StreamingFrameBuffer buffer = new(startupBufferSeconds: 0.1f);
        buffer.SetMetadata(30f, ["jawOpen"]);
        buffer.Append([0.1f]);

        Assert.False(buffer.IsCompleted);
        Assert.Null(buffer.DeclaredFrameCount);

        buffer.MarkComplete(1);

        Assert.True(buffer.IsCompleted);
        Assert.Equal(1, buffer.DeclaredFrameCount);
        Assert.True(buffer.TryGetMetadata(out float outputFps, out IReadOnlyList<string> blendshapeNames));
        Assert.Equal(30f, outputFps);
        Assert.Equal(["jawOpen"], blendshapeNames);
    }

    /// <summary>
    /// The first recorded failure wins and later failures must not overwrite it.
    /// </summary>
    [Fact]
    public void MarkFailed_KeepsFirstFailureAndSetsFaultedState()
    {
        StreamingFrameBuffer buffer = new(startupBufferSeconds: 0.1f);

        InvalidOperationException firstError = new("first failure");
        InvalidOperationException secondError = new("second failure");

        buffer.MarkFailed(firstError);
        buffer.MarkFailed(secondError);

        Assert.True(buffer.IsFaulted);
        Assert.Same(firstError, buffer.Error);
    }

    /// <summary>
    /// The startup gate must open exactly when ceil(startup seconds × fps) frames have arrived: 0.1 s
    /// at 60 fps requires six frames.
    /// </summary>
    [Fact]
    public async Task StartupBufferReady_WithSixtyFpsAndTenthOfSecond_OpensAtSixFrames()
    {
        StreamingFrameBuffer buffer = new(startupBufferSeconds: 0.1f);
        buffer.SetMetadata(60f, ["jawOpen"]);

        for (int frameIndex = 0; frameIndex < 5; frameIndex++)
        {
            buffer.Append([0.5f]);
            await Task.Delay(20);
            Assert.False(buffer.StartupBufferReady.IsCompleted, $"Gate must stay closed at {frameIndex + 1} frame(s).");
        }

        buffer.Append([0.5f]);

        Task gateTask = buffer.StartupBufferReady;
        await gateTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(gateTask.IsCompletedSuccessfully);
    }

    /// <summary>
    /// A zero-second startup buffer still requires at least one frame so playback never starts empty.
    /// </summary>
    [Fact]
    public async Task StartupBufferReady_WithZeroSeconds_OpensAtFirstFrame()
    {
        StreamingFrameBuffer buffer = new(startupBufferSeconds: 0f);
        buffer.SetMetadata(30f, ["jawOpen"]);

        Assert.False(buffer.StartupBufferReady.IsCompleted);

        buffer.Append([0.5f]);

        await buffer.StartupBufferReady.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(buffer.StartupBufferReady.IsCompletedSuccessfully);
    }

    /// <summary>
    /// A short stream that completes with fewer frames than the startup buffer must open the gate early
    /// so preparation settles instead of waiting for frames that will never arrive.
    /// </summary>
    [Fact]
    public async Task StartupBufferReady_WhenShortStreamCompletes_OpensEarly()
    {
        StreamingFrameBuffer buffer = new(startupBufferSeconds: 1f);
        buffer.SetMetadata(30f, ["jawOpen"]);

        buffer.Append([0.2f]);
        buffer.Append([0.4f]);
        Assert.False(buffer.StartupBufferReady.IsCompleted);

        buffer.MarkComplete(2);

        await buffer.StartupBufferReady.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(buffer.StartupBufferReady.IsCompletedSuccessfully);
    }

    /// <summary>
    /// A producer failure before the gate opens must fault the gate with the recorded exception so
    /// preparation fails with the underlying error.
    /// </summary>
    [Fact]
    public async Task StartupBufferReady_WhenProducerFailsBeforeGate_FaultsWithRecordedError()
    {
        StreamingFrameBuffer buffer = new(startupBufferSeconds: 1f);
        buffer.SetMetadata(30f, ["jawOpen"]);
        buffer.Append([0.2f]);

        InvalidOperationException failure = new("stream broke");
        buffer.MarkFailed(failure);

        Task gateTask = buffer.StartupBufferReady;
        _ = await Task.WhenAny(gateTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(gateTask.IsFaulted);
        Assert.Same(failure, gateTask.Exception!.InnerException);
    }

    /// <summary>
    /// Concurrent producers appending while a consumer reads must keep every frame intact and readable.
    /// </summary>
    [Fact]
    public async Task ParallelAppendAndRead_PreservesEveryFrame()
    {
        const int producerCount = 8;
        const int framesPerProducer = 125;
        StreamingFrameBuffer buffer = new(startupBufferSeconds: 30f);
        buffer.SetMetadata(60f, ["jawOpen"]);

        var consumer = Task.Run(async () =>
        {
            while (!buffer.IsCompleted)
            {
                if (buffer.FrameCount > 0)
                {
                    Assert.True(buffer.TryGetFrame(0, out float[] _));
                }

                await Task.Yield();
            }
        });
        await Task.WhenAll(Enumerable.Range(0, producerCount).Select(producerIndex => Task.Run(() =>
        {
            for (int frameIndex = 0; frameIndex < framesPerProducer; frameIndex++)
            {
                buffer.Append([frameIndex % 100 / 100f]);
            }
        })));

        buffer.MarkComplete(producerCount * framesPerProducer);
        await consumer.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(producerCount * framesPerProducer, buffer.FrameCount);
        for (int frameIndex = 0; frameIndex < buffer.FrameCount; frameIndex++)
        {
            Assert.True(buffer.TryGetFrame(frameIndex, out float[] frame));
            _ = Assert.Single(frame);
        }
    }
}
