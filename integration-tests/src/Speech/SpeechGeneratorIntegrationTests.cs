using System.Reflection;
using AlleyCat.Speech.Generation;
using AlleyCat.TestFramework;
using AlleyCat.UI;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Speech;

/// <summary>
/// Runtime coverage for speech-generation completion, failure, and concurrency orchestration.
/// </summary>
public sealed partial class SpeechGeneratorIntegrationTests
{
    private const string FriendlyFailureMessage = "Speech generation failed. Please try again.";

    private static readonly MethodInfo _invokeGenerationAsyncMethod = typeof(SpeechGenerator)
        .GetMethod("InvokeGenerationAsync", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Expected SpeechGenerator.InvokeGenerationAsync for runtime speech tests.");

    /// <summary>
    /// Verifies successful speech generation emits the completion signal and resets lifecycle state.
    /// </summary>
    [Fact]
    [Headless]
    public async Task InvokeGenerationAsync_OnSuccess_EmitsCompletionSignal_AndResetsLifecycleState()
    {
        SceneTree sceneTree = GetSceneTree();
        FakeSpeechGenerator speechGenerator = new()
        {
            NextResultFactory = static async (_, _) => await Task.Run(() => new byte[] { 0x10, 0x20 }),
        };

        sceneTree.Root.AddChild(speechGenerator);
        await WaitForFramesAsync(sceneTree, 2);

        byte[]? generatedAudio = null;
        int completedCount = 0;
        int failedCount = 0;
        _ = speechGenerator.Connect(
            SpeechGenerator.SignalName.SpeechGenerationCompleted,
            Callable.From<byte[]>(audio =>
            {
                completedCount++;
                generatedAudio = audio;
            }));
        _ = speechGenerator.Connect(
            SpeechGenerator.SignalName.SpeechGenerationFailed,
            Callable.From<string>(_ => failedCount++));

        try
        {
            await InvokeGenerationAsync(speechGenerator, "Hello alley cat", instruction: null);
            await WaitForNextFrameAsync(sceneTree);

            Assert.Equal(1, speechGenerator.GenerateCallCount);
            Assert.False(speechGenerator.IsGenerating);
            Assert.Equal(1, completedCount);
            Assert.Equal(0, failedCount);
            Assert.Equal(new byte[] { 0x10, 0x20 }, generatedAudio);
        }
        finally
        {
            speechGenerator.QueueFree();
            await WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>
    /// Verifies failed speech generation emits the failure signal, posts the friendly fallback message, and runs failure hooks on the Godot thread.
    /// </summary>
    [Fact]
    [Headless]
    public async Task InvokeGenerationAsync_OnFailure_EmitsFailureSignal_PostsFriendlyMessage_AndRunsFailureHookOnMainThread()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        (Node global, NotificationWidget notificationWidget) = await CreateNotificationHostAsync(sceneTree);
        int dispatchingThreadId = System.Environment.CurrentManagedThreadId;
        FakeSpeechGenerator speechGenerator = new()
        {
            NextResultFactory = static async (_, _) =>
            {
                await Task.Run(static () => { });
                throw new InvalidOperationException("Backend unavailable");
            },
        };

        sceneTree.Root.AddChild(speechGenerator);
        await WaitForFramesAsync(sceneTree, 2);

        string? failureText = null;
        int? failureSignalThreadId = null;
        int completedCount = 0;
        int failedCount = 0;
        _ = speechGenerator.Connect(
            SpeechGenerator.SignalName.SpeechGenerationCompleted,
            Callable.From<byte[]>(_ => completedCount++));
        _ = speechGenerator.Connect(
            SpeechGenerator.SignalName.SpeechGenerationFailed,
            Callable.From<string>(error =>
            {
                failedCount++;
                failureText = error;
                failureSignalThreadId = System.Environment.CurrentManagedThreadId;
            }));

        try
        {
            await InvokeGenerationAsync(speechGenerator, "Hello alley cat", instruction: null);
            await WaitForNextFrameAsync(sceneTree);

            Assert.Equal(1, speechGenerator.GenerateCallCount);
            Assert.False(speechGenerator.IsGenerating);
            Assert.Equal(0, completedCount);
            Assert.Equal(1, failedCount);
            Assert.Equal("Backend unavailable", failureText);
            Assert.Equal(dispatchingThreadId, failureSignalThreadId);
            Assert.Equal(dispatchingThreadId, speechGenerator.FailureHookThreadId);
            Assert.True(notificationWidget.Visible);
            Assert.Equal(FriendlyFailureMessage, GetNewestNotificationText(notificationWidget));
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, speechGenerator, global);
            await existingGlobalScope.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies a second request is rejected while a generation is already in flight.
    /// </summary>
    [Fact]
    [Headless]
    public async Task GenerateSpeech_WhenGenerationAlreadyInFlight_RejectsAdditionalRequest()
    {
        SceneTree sceneTree = GetSceneTree();
        FakeSpeechGenerator speechGenerator = new();
        TaskCompletionSource<byte[]> firstRequestCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int invocationIndex = 0;
        speechGenerator.NextResultFactory = (_, _) =>
        {
            invocationIndex++;
            return invocationIndex == 1
                ? firstRequestCompletion.Task
                : Task.FromResult<byte[]>([0x02]);
        };

        sceneTree.Root.AddChild(speechGenerator);
        await WaitForFramesAsync(sceneTree, 2);

        try
        {
            speechGenerator.GenerateSpeech("First request");
            await WaitUntilAsync(sceneTree, () => speechGenerator.IsGenerating && speechGenerator.GenerateCallCount == 1, 30);

            speechGenerator.GenerateSpeech("Second request");
            await WaitForFramesAsync(sceneTree, 3);

            Assert.True(speechGenerator.IsGenerating);
            Assert.Equal(1, speechGenerator.GenerateCallCount);

            firstRequestCompletion.SetResult([0x01]);
            await WaitUntilAsync(sceneTree, () => !speechGenerator.IsGenerating, 30);
            await WaitForFramesAsync(sceneTree, 2);

            Assert.False(speechGenerator.IsGenerating);
            Assert.Equal(1, speechGenerator.GenerateCallCount);
        }
        finally
        {
            speechGenerator.QueueFree();
            await WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>
    /// The signal-driven generator path must emit audio after base-level normalisation when a target rate is configured.
    /// </summary>
    [Fact]
    [Headless]
    public async Task InvokeGenerationAsync_WithTargetSampleRate_EmitsNormalisedCompletionAudio()
    {
        SceneTree sceneTree = GetSceneTree();
        FakeSpeechGenerator speechGenerator = new()
        {
            TargetSampleRate = 16000,
            NextResultFactory = static (_, _) => Task.FromResult(CreateWaveFileBytes(
                [0x00, 0x00, 0x10, 0x00, 0x20, 0x00, 0x30, 0x00],
                sampleRate: 8000,
                channelCount: 1,
                bitsPerSample: 16)),
        };

        sceneTree.Root.AddChild(speechGenerator);
        await WaitForFramesAsync(sceneTree, 2);

        byte[]? generatedAudio = null;
        _ = speechGenerator.Connect(
            SpeechGenerator.SignalName.SpeechGenerationCompleted,
            Callable.From<byte[]>(audio => generatedAudio = audio));

        try
        {
            await InvokeGenerationAsync(speechGenerator, "Hello alley cat", instruction: null);
            await WaitForNextFrameAsync(sceneTree);

            Assert.Equal(1, speechGenerator.GenerateCallCount);
            byte[] audio = Assert.IsType<byte[]>(generatedAudio);
            Assert.Equal(16000, ReadWaveSampleRate(audio));
            Assert.Equal(16, ReadWaveDataLength(audio));
        }
        finally
        {
            speechGenerator.QueueFree();
            await WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>
    /// Streamed speech chunks must be emitted before the final completion audio is whole-file normalised.
    /// </summary>
    [Fact]
    [Headless]
    public async Task InvokeGenerationAsync_WithStreamingChunks_EmitsRawChunks_AndNormalisedCompletionAudio()
    {
        SceneTree sceneTree = GetSceneTree();
        byte[] rawChunk = [0xAA, 0xBB, 0xCC];
        FakeSpeechGenerator speechGenerator = new()
        {
            TargetSampleRate = 16000,
            NextStreamingResultFactory = async (_, _, audioChunkHandler) =>
            {
                await audioChunkHandler(rawChunk);
                return CreateWaveFileBytes(
                    [0x00, 0x00, 0x10, 0x00, 0x20, 0x00, 0x30, 0x00],
                    sampleRate: 8000,
                    channelCount: 1,
                    bitsPerSample: 16);
            },
        };

        sceneTree.Root.AddChild(speechGenerator);
        await WaitForFramesAsync(sceneTree, 2);

        List<byte[]> streamedChunks = [];
        byte[]? generatedAudio = null;
        _ = speechGenerator.Connect(
            SpeechGenerator.SignalName.SpeechGenerationChunkReceived,
            Callable.From<byte[]>(streamedChunks.Add));
        _ = speechGenerator.Connect(
            SpeechGenerator.SignalName.SpeechGenerationCompleted,
            Callable.From<byte[]>(audio => generatedAudio = audio));

        try
        {
            await InvokeGenerationAsync(speechGenerator, "Hello alley cat", instruction: null);
            await WaitForNextFrameAsync(sceneTree);

            Assert.Equal(1, speechGenerator.GenerateCallCount);
            byte[] streamedChunk = Assert.Single(streamedChunks);
            Assert.Equal(rawChunk, streamedChunk);

            byte[] audio = Assert.IsType<byte[]>(generatedAudio);
            Assert.Equal(16000, ReadWaveSampleRate(audio));
            Assert.Equal(16, ReadWaveDataLength(audio));
        }
        finally
        {
            speechGenerator.QueueFree();
            await WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>
    /// Resampling requests for unsupported input must fail through the existing failure path instead of emitting corrupted audio.
    /// </summary>
    [Fact]
    [Headless]
    public async Task InvokeGenerationAsync_WhenResamplingUnsupportedAudio_EmitsFailureSignal_AndSkipsCompletionSignal()
    {
        SceneTree sceneTree = GetSceneTree();
        FakeSpeechGenerator speechGenerator = new()
        {
            TargetSampleRate = 16000,
            NextResultFactory = static (_, _) => Task.FromResult<byte[]>([0x01, 0x02, 0x03]),
        };

        sceneTree.Root.AddChild(speechGenerator);
        await WaitForFramesAsync(sceneTree, 2);

        string? failureText = null;
        int completedCount = 0;
        int failedCount = 0;
        _ = speechGenerator.Connect(
            SpeechGenerator.SignalName.SpeechGenerationCompleted,
            Callable.From<byte[]>(_ => completedCount++));
        _ = speechGenerator.Connect(
            SpeechGenerator.SignalName.SpeechGenerationFailed,
            Callable.From<string>(error =>
            {
                failedCount++;
                failureText = error;
            }));

        try
        {
            await InvokeGenerationAsync(speechGenerator, "Hello alley cat", instruction: null);
            await WaitForNextFrameAsync(sceneTree);

            Assert.Equal(1, speechGenerator.GenerateCallCount);
            Assert.False(speechGenerator.IsGenerating);
            Assert.Equal(0, completedCount);
            Assert.Equal(1, failedCount);
            Assert.Contains("Audio resampling failed", failureText, StringComparison.Ordinal);
        }
        finally
        {
            speechGenerator.QueueFree();
            await WaitForFramesAsync(sceneTree, 2);
        }
    }

    private static async Task InvokeGenerationAsync(SpeechGenerator speechGenerator, string text, string? instruction)
    {
        Task invocation = (Task?)_invokeGenerationAsyncMethod.Invoke(speechGenerator, [text, instruction])
            ?? throw new InvalidOperationException("Expected speech-generation invocation task.");

        await invocation;
    }

    private static string GetNewestNotificationText(NotificationWidget notificationWidget)
    {
        VBoxContainer messages = notificationWidget.GetNode<VBoxContainer>("Messages");
        Label newestLabel = Assert.IsType<Label>(messages.GetChild(0), exactMatch: false);
        return newestLabel.Text;
    }

    private static byte[] CreateWaveFileBytes(byte[] data, int sampleRate, short channelCount, short bitsPerSample)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, System.Text.Encoding.ASCII, leaveOpen: true);

        short blockAlign = (short)(channelCount * bitsPerSample / 8);
        int byteRate = sampleRate * blockAlign;

        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + data.Length);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channelCount);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        writer.Write(data.Length);
        writer.Write(data);
        writer.Flush();

        return stream.ToArray();
    }

    private static int ReadWaveSampleRate(byte[] audio)
        => BitConverter.ToInt32(audio, 24);

    private static int ReadWaveDataLength(byte[] audio)
        => BitConverter.ToInt32(audio, 40);

    private static async Task<(Node global, NotificationWidget notificationWidget)> CreateNotificationHostAsync(SceneTree sceneTree)
    {
        Node global = new()
        {
            Name = "Global",
        };

        Node xr = new()
        {
            Name = "XR",
        };

        SubViewport subViewport = new()
        {
            Name = "SubViewport",
            Disable3D = true,
        };

        UIOverlay overlay = new()
        {
            Name = "UIOverlay",
        };

        NotificationWidget notificationWidget = new()
        {
            Name = "NotificationOverlay",
        };

        VBoxContainer messages = new()
        {
            Name = "Messages",
        };

        notificationWidget.AddChild(messages);
        overlay.AddChild(notificationWidget);
        subViewport.AddChild(overlay);
        xr.AddChild(subViewport);
        global.AddChild(xr);
        sceneTree.Root.AddChild(global);
        await WaitForFramesAsync(sceneTree, 2);

        return (global, notificationWidget);
    }

    private static async Task WaitUntilAsync(SceneTree sceneTree, Func<bool> predicate, int maxFrames)
    {
        for (int frame = 0; frame < maxFrames; frame++)
        {
            if (predicate())
            {
                return;
            }

            await WaitForNextFrameAsync(sceneTree);
        }

        Assert.True(predicate(), $"Condition was not met within {maxFrames} frames.");
    }

    private static async Task DestroyFixtureAsync(SceneTree sceneTree, SpeechGenerator speechGenerator, Node global)
    {
        speechGenerator.QueueFree();
        global.QueueFree();
        await WaitForFramesAsync(sceneTree, 2);
    }

    private sealed partial class FakeSpeechGenerator : SpeechGenerator
    {
        public Func<string, string?, Task<byte[]>> NextResultFactory
        {
            get;
            set;
        } = (_, _) => Task.FromResult<byte[]>([]);

        public Func<string, string?, Func<byte[], Task>, Task<byte[]>>? NextStreamingResultFactory
        {
            get;
            set;
        }

        public int GenerateCallCount
        {
            get;
            private set;
        }

        public int? FailureHookThreadId
        {
            get;
            private set;
        }

        protected override Task<byte[]> GenerateCore(string text, string? instruction = null)
        {
            GenerateCallCount++;
            return NextResultFactory(text, instruction);
        }

        protected override Task<byte[]> GenerateStreamingCore(
            string text,
            string? instruction,
            Func<byte[], Task> audioChunkHandler)
        {
            if (NextStreamingResultFactory is null)
            {
                return base.GenerateStreamingCore(text, instruction, audioChunkHandler);
            }

            GenerateCallCount++;
            return NextStreamingResultFactory(text, instruction, audioChunkHandler);
        }

        protected override void OnSpeechGenerationFailed(string error)
        {
            _ = error;
            FailureHookThreadId = System.Environment.CurrentManagedThreadId;
        }
    }

    private sealed class ExistingGlobalScope(SceneTree sceneTree, Node? existingGlobal, string? originalName)
    {
        public static async Task<ExistingGlobalScope> CreateAsync(SceneTree sceneTree)
        {
            Node? existingGlobal = sceneTree.Root.GetNodeOrNull<Node>("Global");
            if (existingGlobal is null)
            {
                return new ExistingGlobalScope(sceneTree, null, null);
            }

            string originalName = existingGlobal.Name;
            existingGlobal.Name = "Global_PreSpeechGeneratorIntegrationTest";
            await WaitForNextFrameAsync(sceneTree);
            return new ExistingGlobalScope(sceneTree, existingGlobal, originalName);
        }

        public async Task DisposeAsync()
        {
            _ = sceneTree;

            if (existingGlobal is null || originalName is null)
            {
                return;
            }

            existingGlobal.Name = originalName;
            await WaitForNextFrameAsync(sceneTree);
        }
    }
}
