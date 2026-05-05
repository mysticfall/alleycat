using System.Reflection;
using AlleyCat.Speech;
using AlleyCat.UI;
using AlleyCat.XR;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Speech;

/// <summary>
/// Runtime coverage for transcription completion and failure orchestration.
/// </summary>
public sealed partial class TranscriberIntegrationTests
{
    private const string FriendlyFailureMessage = "Voice transcription failed. Please try again.";

    private static readonly MethodInfo _invokeTranscriptionAsyncMethod = typeof(Transcriber)
        .GetMethod("InvokeTranscriptionAsync", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Expected Transcriber.InvokeTranscriptionAsync for runtime speech tests.");

    /// <summary>
    /// Verifies successful transcription emits the completion signal, posts the transcript, and clears the in-flight state.
    /// </summary>
    [Fact]
    public async Task InvokeTranscriptionAsync_OnSuccess_EmitsCompletionSignal_PostsTranscript_AndResetsLifecycleState()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        (Node global, NotificationWidget notificationWidget) = await CreateNotificationHostAsync(sceneTree);
        FakeTranscriber transcriber = new()
        {
            NextResultFactory = _ => Task.FromResult("Transcript Ready"),
        };

        sceneTree.Root.AddChild(transcriber);
        await WaitForFramesAsync(sceneTree, 2);

        string? completedText = null;
        int completedCount = 0;
        int failedCount = 0;
        _ = transcriber.Connect(
            Transcriber.SignalName.TranscriptionCompleted,
            Callable.From<string>(text =>
            {
                completedCount++;
                completedText = text;
            }));
        _ = transcriber.Connect(
            Transcriber.SignalName.TranscriptionFailed,
            Callable.From<string>(_ => failedCount++));

        try
        {
            await InvokeTranscriptionAsync(transcriber);
            await WaitForNextFrameAsync(sceneTree);

            Assert.Equal(1, transcriber.TranscribeCallCount);
            Assert.False(transcriber.IsTranscribing);
            Assert.Equal(1, completedCount);
            Assert.Equal("Transcript Ready", completedText);
            Assert.Equal(0, failedCount);
            Assert.True(notificationWidget.Visible);
            Assert.Equal("Transcript Ready", GetNewestNotificationText(notificationWidget));
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, transcriber, global);
            await existingGlobalScope.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies failed transcription emits the failure signal, posts the friendly fallback message, and clears the in-flight state.
    /// </summary>
    [Fact]
    public async Task InvokeTranscriptionAsync_OnFailure_EmitsFailureSignal_PostsFriendlyMessage_AndResetsLifecycleState()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        (Node global, NotificationWidget notificationWidget) = await CreateNotificationHostAsync(sceneTree);
        int dispatchingThreadId = System.Environment.CurrentManagedThreadId;
        FakeTranscriber transcriber = new()
        {
            NextResultFactory = static async _ =>
            {
                await Task.Run(static () => { });
                throw new InvalidOperationException("Backend unavailable");
            },
        };

        sceneTree.Root.AddChild(transcriber);
        await WaitForFramesAsync(sceneTree, 2);

        string? failureText = null;
        int? failureSignalThreadId = null;
        int completedCount = 0;
        int failedCount = 0;
        _ = transcriber.Connect(
            Transcriber.SignalName.TranscriptionCompleted,
            Callable.From<string>(_ => completedCount++));
        _ = transcriber.Connect(
            Transcriber.SignalName.TranscriptionFailed,
            Callable.From<string>(error =>
            {
                failedCount++;
                failureText = error;
                failureSignalThreadId = System.Environment.CurrentManagedThreadId;
            }));

        try
        {
            await InvokeTranscriptionAsync(transcriber);
            await WaitForNextFrameAsync(sceneTree);

            Assert.Equal(1, transcriber.TranscribeCallCount);
            Assert.False(transcriber.IsTranscribing);
            Assert.Equal(0, completedCount);
            Assert.Equal(1, failedCount);
            Assert.Equal("Backend unavailable", failureText);
            Assert.Equal(dispatchingThreadId, failureSignalThreadId);
            Assert.True(notificationWidget.Visible);
            Assert.Equal(FriendlyFailureMessage, GetNewestNotificationText(notificationWidget));
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, transcriber, global);
            await existingGlobalScope.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies the configured XR record button starts recording on press and stops/transcribes on release.
    /// </summary>
    [Fact]
    public async Task XRRecordButton_OnConfiguredPressAndRelease_StartsRecording_StopsRecording_AndTranscribes()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        RuntimeSpeechFixture fixture = await CreateRuntimeSpeechFixtureAsync(sceneTree);

        try
        {
            FakeTranscriber transcriber = Assert.IsType<FakeTranscriber>(fixture.Transcriber);
            fixture.Transcriber.RecordButton = new StringName("speech_record");

            fixture.LeftController.TriggerActionButtonPressed("other_action");
            await WaitForNextFrameAsync(sceneTree);
            Assert.False(fixture.Transcriber.IsRecording);
            Assert.Equal(0, transcriber.TranscribeCallCount);

            fixture.LeftController.TriggerActionButtonPressed("speech_record");
            await WaitForNextFrameAsync(sceneTree);

            Assert.True(fixture.Transcriber.IsRecording);
            Assert.Equal(0, transcriber.TranscribeCallCount);

            fixture.LeftController.TriggerActionButtonReleased("speech_record");
            await WaitUntilAsync(
                sceneTree,
                () => !fixture.Transcriber.IsRecording
                    && !fixture.Transcriber.IsTranscribing
                    && transcriber.TranscribeCallCount == 1,
                maxFrames: 30);

            Assert.False(fixture.Transcriber.IsRecording);
            Assert.False(fixture.Transcriber.IsTranscribing);
            Assert.Equal(1, transcriber.TranscribeCallCount);
        }
        finally
        {
            await DestroyRuntimeSpeechFixtureAsync(sceneTree, fixture);
            await existingGlobalScope.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies recording auto-stops and transcribes when the maximum duration timer expires.
    /// </summary>
    [Fact]
    public async Task Recording_WhenMaxDurationExpires_AutoStopsAndTranscribes()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        RuntimeSpeechFixture fixture = await CreateRuntimeSpeechFixtureAsync(sceneTree);

        try
        {
            FakeTranscriber transcriber = Assert.IsType<FakeTranscriber>(fixture.Transcriber);
            fixture.Transcriber.RecordButton = new StringName("speech_record");
            fixture.Transcriber.MaxRecordingDuration = 0.1f;

            fixture.LeftController.TriggerActionButtonPressed("speech_record");
            await WaitForNextFrameAsync(sceneTree);

            Assert.True(fixture.Transcriber.IsRecording);
            Assert.Equal(0, transcriber.TranscribeCallCount);

            await WaitForSecondsAsync(sceneTree, 0.25);
            await WaitUntilAsync(
                sceneTree,
                () => !fixture.Transcriber.IsRecording
                    && !fixture.Transcriber.IsTranscribing
                    && transcriber.TranscribeCallCount == 1,
                maxFrames: 60);

            Assert.False(fixture.Transcriber.IsRecording);
            Assert.False(fixture.Transcriber.IsTranscribing);
            Assert.Equal(1, transcriber.TranscribeCallCount);
        }
        finally
        {
            await DestroyRuntimeSpeechFixtureAsync(sceneTree, fixture);
            await existingGlobalScope.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies disabled transcribers ignore XR and manual start attempts.
    /// </summary>
    [Fact]
    public async Task StartRecording_WhenDisabled_DoesNotStartFromXRorManualPaths()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        RuntimeSpeechFixture fixture = await CreateRuntimeSpeechFixtureAsync(sceneTree);

        try
        {
            FakeTranscriber transcriber = Assert.IsType<FakeTranscriber>(fixture.Transcriber);
            fixture.Transcriber.Enabled = false;
            fixture.Transcriber.RecordButton = new StringName("speech_record");

            fixture.LeftController.TriggerActionButtonPressed("speech_record");
            await WaitForNextFrameAsync(sceneTree);

            Assert.False(fixture.Transcriber.IsRecording);
            Assert.False(fixture.Transcriber.IsTranscribing);

            fixture.Transcriber.StartRecording();
            await WaitForNextFrameAsync(sceneTree);

            Assert.False(fixture.Transcriber.IsRecording);
            Assert.False(fixture.Transcriber.IsTranscribing);
            Assert.Equal(0, transcriber.TranscribeCallCount);
        }
        finally
        {
            await DestroyRuntimeSpeechFixtureAsync(sceneTree, fixture);
            await existingGlobalScope.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies disabling before stop prevents a new transcription request from starting.
    /// </summary>
    [Fact]
    public async Task StopRecording_WhenDisabledBeforeStop_DoesNotStartTranscriptionRequest()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        RuntimeSpeechFixture fixture = await CreateRuntimeSpeechFixtureAsync(sceneTree);

        try
        {
            FakeTranscriber transcriber = Assert.IsType<FakeTranscriber>(fixture.Transcriber);

            fixture.Transcriber.StartRecording();
            await WaitForNextFrameAsync(sceneTree);
            Assert.True(fixture.Transcriber.IsRecording);

            fixture.Transcriber.Enabled = false;
            fixture.Transcriber.StopRecording();
            await WaitUntilAsync(
                sceneTree,
                () => !fixture.Transcriber.IsRecording && !fixture.Transcriber.IsTranscribing,
                maxFrames: 30);

            Assert.False(fixture.Transcriber.IsRecording);
            Assert.False(fixture.Transcriber.IsTranscribing);
            Assert.Equal(0, transcriber.TranscribeCallCount);
        }
        finally
        {
            await DestroyRuntimeSpeechFixtureAsync(sceneTree, fixture);
            await existingGlobalScope.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies OpenAI transcriber debug output posts start, stop, and result notifications without breaking lifecycle flow.
    /// </summary>
    [Fact]
    public async Task OpenAITranscriber_DebugNotificationsEnabled_PostsStartStopAndResultMessages()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        int dispatchingThreadId = System.Environment.CurrentManagedThreadId;
        int? backgroundThreadId = null;
        FakeOpenAITranscriber transcriber = new()
        {
            Name = "Transcriber",
            DebugNotificationOutputEnabled = true,
            NextResultFactory = async _ => await Task.Run(() =>
                {
                    backgroundThreadId = System.Environment.CurrentManagedThreadId;
                    return "XR Debug Transcript";
                }),
        };

        RuntimeSpeechFixture fixture = await CreateRuntimeSpeechFixtureAsync(sceneTree, transcriber);

        try
        {
            string? completedText = null;
            int completedCount = 0;
            int? completionSignalThreadId = null;
            _ = transcriber.Connect(
                Transcriber.SignalName.TranscriptionCompleted,
                Callable.From<string>(text =>
                {
                    completedCount++;
                    completedText = text;
                    completionSignalThreadId = System.Environment.CurrentManagedThreadId;
                }));

            fixture.Transcriber.RecordButton = new StringName("speech_record");

            fixture.LeftController.TriggerActionButtonPressed("speech_record");
            await WaitUntilAsync(
                sceneTree,
                () => GetNotificationTexts(fixture.NotificationWidget)
                    .Contains("Speech debug: Recording started.", StringComparer.Ordinal),
                maxFrames: 30);

            fixture.LeftController.TriggerActionButtonReleased("speech_record");
            await WaitUntilAsync(
                sceneTree,
                () => !fixture.Transcriber.IsRecording
                    && !fixture.Transcriber.IsTranscribing
                    && transcriber.TranscribeCallCount == 1
                     && HasNotification(fixture.NotificationWidget, "Speech debug: Recording stopped.")
                     && HasNotification(fixture.NotificationWidget, "XR Debug Transcript")
                     && HasNotification(fixture.NotificationWidget, "Speech debug: Transcription result: XR Debug Transcript"),
                maxFrames: 60);

            Assert.True(backgroundThreadId.HasValue);
            Assert.NotEqual(dispatchingThreadId, backgroundThreadId);
            Assert.False(fixture.Transcriber.IsRecording);
            Assert.False(fixture.Transcriber.IsTranscribing);
            Assert.Equal(1, transcriber.TranscribeCallCount);
            Assert.True(HasNotification(fixture.NotificationWidget, "Speech debug: Recording started."));
            Assert.True(HasNotification(fixture.NotificationWidget, "Speech debug: Recording stopped."));
            Assert.True(HasNotification(fixture.NotificationWidget, "XR Debug Transcript"));
            Assert.True(HasNotification(fixture.NotificationWidget, "Speech debug: Transcription result: XR Debug Transcript"));
            Assert.Equal(1, completedCount);
            Assert.Equal("XR Debug Transcript", completedText);
            Assert.Equal(dispatchingThreadId, completionSignalThreadId);
        }
        finally
        {
            await DestroyRuntimeSpeechFixtureAsync(sceneTree, fixture);
            await existingGlobalScope.DisposeAsync();
        }
    }

    private static async Task InvokeTranscriptionAsync(Transcriber transcriber)
    {
        Task invocation = (Task?)_invokeTranscriptionAsyncMethod.Invoke(transcriber, [CreateRecording()])
            ?? throw new InvalidOperationException("Expected transcription invocation task.");

        await invocation;
    }

    private static AudioStreamWav CreateRecording()
        => new()
        {
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = 16000,
            Stereo = false,
            Data = [0x01, 0x02, 0x03, 0x04],
        };

    private static string GetNewestNotificationText(NotificationWidget notificationWidget)
    {
        VBoxContainer messages = notificationWidget.GetNode<VBoxContainer>("Messages");
        Label newestLabel = Assert.IsType<Label>(messages.GetChild(0), exactMatch: false);
        return newestLabel.Text;
    }

    private static bool HasNotification(NotificationWidget notificationWidget, string text)
        => GetNotificationTexts(notificationWidget).Contains(text, StringComparer.Ordinal);

    private static IReadOnlyList<string> GetNotificationTexts(NotificationWidget notificationWidget)
    {
        VBoxContainer messages = notificationWidget.GetNode<VBoxContainer>("Messages");
        List<string> notificationTexts = [];

        foreach (Node child in messages.GetChildren())
        {
            if (child is Label label)
            {
                notificationTexts.Add(label.Text);
            }
        }

        return notificationTexts;
    }

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

    private static async Task<RuntimeSpeechFixture> CreateRuntimeSpeechFixtureAsync(SceneTree sceneTree)
        => await CreateRuntimeSpeechFixtureAsync(
            sceneTree,
            new FakeTranscriber
            {
                Name = "Transcriber",
                NextResultFactory = _ => Task.FromResult("XR Transcript"),
            });

    private static async Task<RuntimeSpeechFixture> CreateRuntimeSpeechFixtureAsync(SceneTree sceneTree, Transcriber transcriber)
    {
        Node global = new()
        {
            Name = "Global",
        };

        FakeXRManager xrManager = new()
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
        xrManager.AddChild(subViewport);
        global.AddChild(xrManager);

        global.AddChild(transcriber);
        sceneTree.Root.AddChild(global);
        await WaitForFramesAsync(sceneTree, 3);

        return new RuntimeSpeechFixture(global, xrManager, transcriber, xrManager.LeftController, notificationWidget);
    }

    private static async Task DestroyRuntimeSpeechFixtureAsync(SceneTree sceneTree, RuntimeSpeechFixture fixture)
    {
        fixture.Global.QueueFree();
        await WaitForFramesAsync(sceneTree, 2);
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

    private static async Task DestroyFixtureAsync(SceneTree sceneTree, Transcriber transcriber, Node global)
    {
        transcriber.QueueFree();
        global.QueueFree();
        await WaitForFramesAsync(sceneTree, 2);
    }

    private sealed partial class FakeTranscriber : Transcriber
    {
        public Func<AudioStreamWav, Task<string>> NextResultFactory
        {
            get;
            set;
        } = _ => Task.FromResult(string.Empty);

        public int TranscribeCallCount
        {
            get;
            private set;
        }

        public override Task<string> Transcribe(AudioStreamWav audioStream)
        {
            TranscribeCallCount++;
            return NextResultFactory(audioStream);
        }
    }

    private sealed partial class FakeOpenAITranscriber : OpenAITranscriber
    {
        public Func<AudioStreamWav, Task<string>> NextResultFactory
        {
            get;
            set;
        } = _ => Task.FromResult(string.Empty);

        public int TranscribeCallCount
        {
            get;
            private set;
        }

        public override Task<string> Transcribe(AudioStreamWav audioStream)
        {
            TranscribeCallCount++;
            return NextResultFactory(audioStream);
        }
    }

    private sealed record RuntimeSpeechFixture(
        Node Global,
        XRManager XRManager,
        Transcriber Transcriber,
        FakeXRHandController LeftController,
        NotificationWidget NotificationWidget);

    private sealed partial class FakeXRManager : XRManager
    {
        private static readonly FieldInfo _runtimeBackingField = typeof(XRManager)
            .GetField("<Runtime>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected XRManager runtime backing field for speech tests.");

        private readonly FakeXRRuntime _runtime = new();

        public FakeXRHandController LeftController => _runtime.LeftControllerNode;

        public override void _Ready()
        {
            _runtimeBackingField.SetValue(this, _runtime);
            InitialisationAttempted = true;
            InitialisationSucceeded = true;
            _ = EmitSignal("Initialised", true);
        }
    }

    private sealed class FakeXRRuntime : IXRRuntime
    {
        public FakeXRRuntime()
        {
            OriginNode = new Node3D();
            CameraNode = new Camera3D();
            LeftControllerNode = new FakeXRHandController();
            RightControllerNode = new FakeXRHandController();
        }

        public IXROrigin Origin => new FakeXROrigin(OriginNode);

        public IXRCamera Camera => new FakeXRCamera(CameraNode);

        public IXRHandController RightHandController => RightControllerNode;

        public IXRHandController LeftHandController => LeftControllerNode;

#pragma warning disable CS0067
        public event Action? PoseRecentered;
#pragma warning restore CS0067

        public Node3D OriginNode
        {
            get;
        }

        public Camera3D CameraNode
        {
            get;
        }

        public FakeXRHandController LeftControllerNode
        {
            get;
        }

        public FakeXRHandController RightControllerNode
        {
            get;
        }

        public bool Initialise(SubViewport viewport, int maximumRefreshRate)
        {
            _ = viewport;
            _ = maximumRefreshRate;
            return true;
        }
    }

    private sealed partial class FakeXRHandController : Node3D, IXRHandController
    {
        public event Action<string>? ActionButtonPressed;

        public event Action<string>? ActionButtonReleased;

#pragma warning disable CS0067
        public event Action<string, float>? ActionFloatInputChanged;

        public event Action<string, Vector2>? ActionVector2InputChanged;
#pragma warning restore CS0067

        public Node3D ControllerNode => this;

        public Node3D HandPositionNode => this;

        public void TriggerActionButtonPressed(string actionName)
            => ActionButtonPressed?.Invoke(actionName);

        public void TriggerActionButtonReleased(string actionName)
            => ActionButtonReleased?.Invoke(actionName);
    }

    private sealed record FakeXROrigin(Node3D OriginNode) : IXROrigin
    {
        public float WorldScale { get; set; } = 1.0f;
    }

    private sealed record FakeXRCamera(Camera3D CameraNode) : IXRCamera;

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
            existingGlobal.Name = "Global_PreSpeechIntegrationTest";
            await WaitForNextFrameAsync(sceneTree);
            return new ExistingGlobalScope(sceneTree, existingGlobal, originalName);
        }

        public async Task DisposeAsync()
        {
            if (existingGlobal is null || originalName is null)
            {
                return;
            }

            existingGlobal.Name = originalName;
            await WaitForNextFrameAsync(sceneTree);
        }
    }
}
