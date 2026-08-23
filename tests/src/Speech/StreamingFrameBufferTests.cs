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
    /// Lifecycle diagnostics must obtain all producer fields from one locked buffer view, including the
    /// final append timing, rather than combining independently-read values.
    /// </summary>
    [Fact]
    public void CaptureDiagnosticSnapshot_AfterComplete_ContainsConsistentTerminalState()
    {
        StreamingFrameBuffer buffer = new(startupBufferSeconds: 0.1f);
        buffer.SetMetadata(60f, ["jawOpen"]);
        buffer.Append([0.25f]);
        buffer.Append([0.75f]);
        buffer.MarkComplete(2);

        StreamingFrameBufferDiagnosticSnapshot snapshot = buffer.CaptureDiagnosticSnapshot();

        Assert.Equal(2, snapshot.FrameCount);
        Assert.Equal(60f, snapshot.OutputFps);
        Assert.Equal(2, snapshot.DeclaredFrameCount);
        Assert.True(snapshot.IsCompleted);
        Assert.False(snapshot.IsFaulted);
        _ = Assert.NotNull(snapshot.LastAppendTimestampUtc);
        _ = Assert.NotNull(snapshot.LastAppendAge);
        Assert.True(snapshot.LastAppendAge >= TimeSpan.Zero);
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
    /// A canonical 30 fps stream adds the conservative duration allowance even for a short speech clip.
    /// </summary>
    [Fact]
    public void StartupBufferReady_WithThirtyFpsAndShortDuration_OpensAtFourFrames()
    {
        StreamingFrameBuffer buffer = new(startupBufferSeconds: 0.1f, playableDurationSeconds: 0.1d);
        buffer.SetMetadata(30f, ["jawOpen"]);

        buffer.Append([0.1f]);
        buffer.Append([0.2f]);
        buffer.Append([0.3f]);
        Assert.False(buffer.StartupBufferReady.IsCompleted);

        buffer.Append([0.4f]);

        Assert.True(buffer.StartupBufferReady.IsCompletedSuccessfully);
    }

    /// <summary>
    /// A canonical 30 fps stream scales its nonzero gate for a longer prepared speech clip.
    /// </summary>
    [Fact]
    public void StartupBufferReady_WithThirtyFpsAndLongDuration_OpensAtSixFrames()
    {
        StreamingFrameBuffer buffer = new(startupBufferSeconds: 0.1f, playableDurationSeconds: 3d);
        buffer.SetMetadata(30f, ["jawOpen"]);

        for (int frameIndex = 0; frameIndex < 5; frameIndex++)
        {
            buffer.Append([0.5f]);
        }

        Assert.False(buffer.StartupBufferReady.IsCompleted);

        buffer.Append([0.5f]);

        Assert.True(buffer.StartupBufferReady.IsCompletedSuccessfully);
    }

    /// <summary>
    /// At 60 fps the duration allowance remains active rather than falling back to only the configured
    /// startup buffer.
    /// </summary>
    [Fact]
    public void StartupBufferReady_WithSixtyFpsAndDuration_OpensAtNineFrames()
    {
        StreamingFrameBuffer buffer = new(startupBufferSeconds: 0.1f, playableDurationSeconds: 0.1d);
        buffer.SetMetadata(60f, ["jawOpen"]);

        for (int frameIndex = 0; frameIndex < 8; frameIndex++)
        {
            buffer.Append([0.5f]);
        }

        Assert.False(buffer.StartupBufferReady.IsCompleted);

        buffer.Append([0.5f]);

        Assert.True(buffer.StartupBufferReady.IsCompletedSuccessfully);
    }

    /// <summary>
    /// Producers above 35 fps add a duration-scaled deficit allowance to the configured startup buffer.
    /// </summary>
    [Fact]
    public void StartupBufferReady_WithAboveThresholdFps_AddsDurationScaledAllowance()
    {
        StreamingFrameBuffer buffer = new(startupBufferSeconds: 0.1f, playableDurationSeconds: 2d);
        buffer.SetMetadata(40f, ["jawOpen"]);

        for (int frameIndex = 0; frameIndex < 13; frameIndex++)
        {
            buffer.Append([0.5f]);
        }

        Assert.False(buffer.StartupBufferReady.IsCompleted);

        buffer.Append([0.5f]);

        Assert.True(buffer.StartupBufferReady.IsCompletedSuccessfully);
    }

    /// <summary>
    /// Duration-scaled buffering is capped at five seconds of the actual metadata frame rate.
    /// </summary>
    [Fact]
    public void StartupBufferReady_WithLargeDuration_CapsAtFiveSeconds()
    {
        StreamingFrameBuffer buffer = new(startupBufferSeconds: 5f, playableDurationSeconds: 120d);
        buffer.SetMetadata(60f, ["jawOpen"]);

        for (int frameIndex = 0; frameIndex < 299; frameIndex++)
        {
            buffer.Append([0.5f]);
        }

        Assert.False(buffer.StartupBufferReady.IsCompleted);

        buffer.Append([0.5f]);

        Assert.True(buffer.StartupBufferReady.IsCompletedSuccessfully);
    }

    /// <summary>
    /// A zero-second startup buffer still requires at least one frame so playback never starts empty.
    /// </summary>
    [Fact]
    public async Task StartupBufferReady_WithZeroSeconds_OpensAtFirstFrame()
    {
        StreamingFrameBuffer buffer = new(startupBufferSeconds: 0f, playableDurationSeconds: 120d);
        buffer.SetMetadata(30f, ["jawOpen"]);

        Assert.False(buffer.StartupBufferReady.IsCompleted);

        buffer.Append([0.5f]);

        await buffer.StartupBufferReady.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(buffer.StartupBufferReady.IsCompletedSuccessfully);
    }

    /// <summary>
    /// A 60 fps metadata record alone must not open a zero-buffer gate; exactly the first valid frame
    /// opens it, independent of the metadata rate.
    /// </summary>
    [Fact]
    public void StartupBufferReady_WithZeroSecondsAndSixtyFps_OpensExactlyAtFirstFrame()
    {
        StreamingFrameBuffer buffer = new(startupBufferSeconds: 0f, playableDurationSeconds: 120d);
        buffer.SetMetadata(60f, ["jawOpen"]);

        Assert.False(buffer.StartupBufferReady.IsCompleted);

        buffer.Append([0.5f]);

        Assert.True(buffer.StartupBufferReady.IsCompletedSuccessfully);
    }

    /// <summary>
    /// Closing the consumer is idempotent and safely discards late producer frames without faulting.
    /// </summary>
    [Fact]
    public void CloseConsumer_WhenLateFramesArrive_DiscardsThemWithoutFault()
    {
        StreamingFrameBuffer buffer = new(startupBufferSeconds: 0.1f);
        buffer.SetMetadata(30f, ["jawOpen"]);
        buffer.Append([0.1f]);

        buffer.CloseConsumer();
        buffer.CloseConsumer();

        buffer.Append([0.9f]);
        Assert.True(buffer.IsConsumerClosed);
        Assert.False(buffer.IsFaulted);
        Assert.Equal(1, buffer.FrameCount);
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
