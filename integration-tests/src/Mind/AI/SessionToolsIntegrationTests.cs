using AlleyCat.Character;
using AlleyCat.Context;
using AlleyCat.Core;
using AlleyCat.Core.Content;
using AlleyCat.Core.Threading;
using AlleyCat.Core.Time;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Tool;
using AlleyCat.Mind.Attention;
using AlleyCat.Mind.Observation;
using AlleyCat.Scene;
using AlleyCat.Speech.Voice;
using AlleyCat.TestFramework;
using AlleyCat.Vision;
using Godot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using AgentObservation = AlleyCat.Mind.Observation.Observation;
using MindBase = AlleyCat.Mind.Mind;

namespace AlleyCat.IntegrationTests.Mind.AI;

/// <summary>
/// Godot-runtime coverage for the production session tools: <c>speak</c> turn-taking and cut-short boundaries,
/// <c>wait</c> result composition, and the read-only timeline <c>history</c> tool.
/// </summary>
[Headless]
public sealed partial class SessionToolsIntegrationTests
{
    private const string CutShortBeforeSpoken =
        "Your speech was cut short by another event before it could be spoken.";

    private const string CutShort = "Your speech was cut short by another event.";

    /// <summary>
    /// Blank speech is rejected through the voice contract without submitting or observing anything
    /// (AI-002 TR-25).
    /// </summary>
    [Fact]
    public async Task Speak_WithBlankInput_RejectsWithoutSubmissionOrObservation()
    {
        await using ToolFixture fixture = new();
        await fixture.ReadyAsync();

        _ = await Assert.ThrowsAnyAsync<ArgumentException>(
            () => fixture.InvokeSpeakAsync("   ", CancellationToken.None).AsTask());

        Assert.Empty(fixture.OwnerVoice.Submissions);
        Assert.Empty(fixture.Mind.GetTimelineForTest());
    }

    /// <summary>
    /// Speech blocks while an attended speaker's window is open, is unblocked by the attended-speaker-finished
    /// cue, and commits exactly one actor-stamped self observation at playback hand-off (AI-002 TR-25/26).
    /// </summary>
    [Fact]
    public async Task Speak_WhileAttendedSpeakerSpeaks_BlocksUntilCueThenSpeaksOnce()
    {
        await using ToolFixture fixture = new();
        await fixture.ReadyAsync();
        fixture.SpeakerVoice.BeginSpeech();

        Task<object?> speakTask = fixture.InvokeSpeakAsync("Hello there.", CancellationToken.None).AsTask();
        await ToolFixture.WaitForFramesAsync(2);
        Assert.False(speakTask.IsCompleted, "Speech must block while the attended speaker speaks.");
        Assert.Empty(fixture.OwnerVoice.Submissions);

        fixture.SpeakerVoice.EndSpeech();
        await fixture.OwnerVoice.SubmissionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.OwnerVoice.CompleteHandOff();
        object? result = await speakTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("Spoken through the configured voice.", result);
        Assert.Equal(["Hello there."], fixture.OwnerVoice.Submissions);
        ObservedSpeech committed = Assert.IsType<ObservedSpeech>(Assert.Single(fixture.Mind.GetTimelineForTest()));
        Assert.Equal(fixture.Owner.FullId, committed.ActorId);
        Assert.Null(committed.VoiceId);
        Assert.Equal("Hello there.", committed.Content);
    }

    /// <summary>
    /// The owning character's own speaking voice and an unattributable voice never block speech
    /// (AI-002 TR-25).
    /// </summary>
    [Fact]
    public async Task Speak_WithOwnOrUnattributableVoiceSpeaking_NeverBlocks()
    {
        await using ToolFixture fixture = new();
        await fixture.ReadyAsync();
        fixture.OwnerVoice.BeginSpeakingWindow();
        fixture.TwinVoice.BeginSpeech();

        Task<object?> speakTask = fixture.InvokeSpeakAsync("No blocking.", CancellationToken.None).AsTask();
        await fixture.OwnerVoice.SubmissionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.OwnerVoice.CompleteHandOff();
        object? result = await speakTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("Spoken through the configured voice.", result);
        Assert.Equal(["No blocking."], fixture.OwnerVoice.Submissions);
    }

    /// <summary>
    /// Interruption while speech is blocked returns the non-throwing cut-short result: nothing was submitted and
    /// nothing was observed (AI-002 TR-27).
    /// </summary>
    [Fact]
    public async Task Speak_CancelledWhileBlocked_ReturnsCutShortResultWithoutObservation()
    {
        await using ToolFixture fixture = new();
        await fixture.ReadyAsync();
        fixture.SpeakerVoice.BeginSpeech();
        using CancellationTokenSource cancellation = new();

        Task<object?> speakTask = fixture.InvokeSpeakAsync("Interrupted.", cancellation.Token).AsTask();
        await ToolFixture.WaitForFramesAsync(2);
        cancellation.Cancel();
        object? result = await speakTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(CutShortBeforeSpoken, result);
        Assert.Empty(fixture.OwnerVoice.Submissions);
        Assert.Empty(fixture.Mind.GetTimelineForTest());
    }

    /// <summary>
    /// Cancellation before playback hand-off withdraws the submission silently: no observed speech, no failure
    /// broadcast, and the cut-short result (AI-002 TR-26/27, SPCH-005 TR-25).
    /// </summary>
    [Fact]
    public async Task Speak_CancelledBeforeHandOff_ReturnsCutShortResultSilently()
    {
        await using ToolFixture fixture = new();
        await fixture.ReadyAsync();
        using CancellationTokenSource cancellation = new();

        Task<object?> speakTask = fixture.InvokeSpeakAsync("Withdrawn.", cancellation.Token).AsTask();
        await fixture.OwnerVoice.SubmissionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        object? result = await speakTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(CutShortBeforeSpoken, result);
        Assert.True(fixture.OwnerVoice.CancellationObserved, "The pre-hand-off submission must observe the caller token.");
        Assert.Empty(fixture.Mind.GetTimelineForTest());
    }

    /// <summary>
    /// Cancellation after playback hand-off cuts the audible speech through the shared cut capability, keeps the
    /// committed observation, and reports the cut-short result (AI-002 TR-27).
    /// </summary>
    [Fact]
    public async Task Speak_CancelledAfterHandOff_CutsVoiceAndKeepsCommittedObservation()
    {
        await using ToolFixture fixture = new(addAiVoice: true);
        await fixture.ReadyAsync();
        using CancellationTokenSource cancellation = new();

        Task<object?> speakTask = fixture.InvokeSpeakAsync("Cut mid-flight.", cancellation.Token).AsTask();
        await fixture.HandOffVoice!.HandOffStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(fixture.HandOffVoice.IsSpeaking, "Hand-off opens the speaking window before the cut.");
        cancellation.Cancel();
        fixture.HandOffVoice.CompleteHandOff();
        object? result = await speakTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(CutShort, result);
        Assert.False(fixture.HandOffVoice.IsSpeaking, "The shared cut capability must close the speaking window.");
        Assert.Equal(1, fixture.HandOffVoice.SpeechEndedCount);
        ObservedSpeech committed = Assert.IsType<ObservedSpeech>(Assert.Single(fixture.Mind.GetTimelineForTest()));
        Assert.Equal(fixture.Owner.FullId, committed.ActorId);
        Assert.Equal("Cut mid-flight.", committed.Content);
    }

    /// <summary>
    /// The wait tool requires a session game clock and fails clearly without one.
    /// </summary>
    [Fact]
    public async Task Wait_WithoutSessionClock_FailsClearly()
    {
        await using ToolFixture fixture = new(clock: null);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.InvokeWaitReadylessAsync(CancellationToken.None).AsTask());

        Assert.Contains("game clock", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A wait result states the delivered notable observations, the elapsed duration, and the current game time
    /// (AI-002 TR-32, TR-37).
    /// </summary>
    [Fact]
    public async Task Wait_ComposesNotablesElapsedAndGameTime()
    {
        await using ToolFixture fixture = new();
        await fixture.ReadyAsync();
        fixture.Mind.ObserveForTest(new TypedObservation("test.alpha", 1f));

        object? result = await fixture.InvokeWaitAsync(null, CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));

        string message = Assert.IsType<string>(result);
        Assert.Contains("Waited 0.0 seconds.", message, StringComparison.Ordinal);
        Assert.Contains("Current game time: 100.0s.", message, StringComparison.Ordinal);
        Assert.Contains("Notable observations:", message, StringComparison.Ordinal);
        Assert.Contains("test.alpha", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A quiet wait reports its elapsed game-time duration and the game timestamp, and the omitted duration
    /// falls back to the configured maximum.
    /// </summary>
    [Fact]
    public async Task Wait_WithQuietExpiry_ReportsElapsedAndGameTimeUsingMindMaximum()
    {
        await using ToolFixture fixture = new();
        await fixture.ReadyAsync();
        fixture.Mind.MaxObservationWaitSeconds = 0.4f;

        Task<object?> waitTask = fixture.InvokeWaitAsync(null, CancellationToken.None).AsTask();
        await ToolFixture.WaitForFramesAsync(2);
        fixture.Clock.NowSeconds = 102.5d;
        object? result = await waitTask.WaitAsync(TimeSpan.FromSeconds(2));

        string message = Assert.IsType<string>(result);
        Assert.Contains("Waited 2.5 seconds.", message, StringComparison.Ordinal);
        Assert.Contains("Current game time: 102.5s.", message, StringComparison.Ordinal);
        Assert.Contains("Nothing notable happened.", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An attended speaker finishing during the wait surfaces the cue phrase in the wait result
    /// (AI-002 TR-33).
    /// </summary>
    [Fact]
    public async Task Wait_WhenAttendedSpeakerFinishes_ReportsCuePhrase()
    {
        await using ToolFixture fixture = new();
        await fixture.ReadyAsync();
        fixture.SpeakerVoice.BeginSpeech();

        Task<object?> waitTask = fixture.InvokeWaitAsync(5f, CancellationToken.None).AsTask();
        await ToolFixture.WaitForFramesAsync(2);
        fixture.SpeakerVoice.EndSpeech();
        object? result = await waitTask.WaitAsync(TimeSpan.FromSeconds(2));

        string message = Assert.IsType<string>(result);
        Assert.Contains("An attended speaker finished speaking.", message, StringComparison.Ordinal);
        Assert.Contains("Nothing notable happened.", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The history tool answers from the Mind timeline in timeline order and stays read-only (AI-002 TR-36).
    /// </summary>
    [Fact]
    public async Task History_ReturnsTimelineOrderAndStaysReadOnly()
    {
        await using ToolFixture fixture = new();
        await fixture.ReadyAsync();
        object? empty = await fixture.InvokeHistoryAsync(null, CancellationToken.None);
        Assert.Equal("You remember no past events yet.", empty);

        fixture.Mind.ObserveForTest(new TypedObservation("test.alpha", 1f));
        fixture.Mind.ObserveForTest(new TypedObservation("test.beta", 0.5f));
        fixture.Mind.ObserveForTest(new TypedObservation("test.gamma", 1f));

        string complete = Assert.IsType<string>(await fixture.InvokeHistoryAsync(null, CancellationToken.None));
        Assert.Contains("3 past event(s), oldest first:", complete, StringComparison.Ordinal);
        int alpha = complete.IndexOf("test.alpha", StringComparison.Ordinal);
        int beta = complete.IndexOf("test.beta", StringComparison.Ordinal);
        int gamma = complete.IndexOf("test.gamma", StringComparison.Ordinal);
        Assert.True(alpha >= 0 && beta > alpha && gamma > beta, "The history result must preserve timeline order.");

        string recent = Assert.IsType<string>(await fixture.InvokeHistoryAsync(2, CancellationToken.None));
        Assert.Contains("2 past event(s), oldest first:", recent, StringComparison.Ordinal);
        Assert.DoesNotContain("test.alpha", recent, StringComparison.Ordinal);
        Assert.Contains("test.beta", recent, StringComparison.Ordinal);
        Assert.Contains("test.gamma", recent, StringComparison.Ordinal);

        Assert.Equal(3, fixture.Mind.GetTimelineForTest().Count);
    }

    private sealed record TypedObservation(string Key, float Importance) : AgentObservation
    {
        public override string TypeKey => Key;

        public override float CalculateImportance(ObservationContext context) => Importance;
    }

    /// <summary>
    /// Assembles the in-tree Mind, voiced scene membership, session tool functions, and a controllable game
    /// clock shared by the tool tests.
    /// </summary>
    private sealed class ToolFixture : IAsyncDisposable
    {
        private readonly SpeechTool _speechTool = new();
        private readonly WaitTool _waitTool = new();
        private readonly HistoryTool _historyTool = new();
        private AIFunction? _speakFunction;
        private AIFunction? _waitFunction;
        private AIFunction? _historyFunction;

        public ToolFixture(bool addAiVoice = false, FakeGameClock? clock = null)
        {
            Clock = clock ?? new FakeGameClock { NowSeconds = 100d };
            OwnerVoice = new ControllableVoice("owner-voice");
            SpeakerVoice = new WindowedVoice("speaker-voice");
            TwinVoice = new WindowedVoice("twin-voice");
            Speaker = new VoiceCharacter("speaker", SpeakerVoice);
            TwinA = new VoiceCharacter("twin-a", TwinVoice);
            TwinB = new VoiceCharacter("twin-b", TwinVoice);
            HandOffVoice = addAiVoice ? new HandOffAIVoice() : null;
            Owner = new VoiceCharacter("owner", HandOffVoice is null ? OwnerVoice : HandOffVoice);
            Membership = [Owner, Speaker, TwinA, TwinB];
            Mind = new TestMind(Owner)
            {
                AttentionDecayPerSecond = 0f,
                ObservationImportanceThreshold = 1f,
            };
            Mind.SetSceneContextLoaderForTesting(() => new TestSceneContext(Membership));
            Mind.AttendForTest(Speaker.FullId);
        }

        public FakeGameClock Clock
        {
            get;
        }

        public ControllableVoice OwnerVoice
        {
            get;
        }

        public WindowedVoice SpeakerVoice
        {
            get;
        }

        public WindowedVoice TwinVoice
        {
            get;
        }

        public HandOffAIVoice? HandOffVoice
        {
            get;
        }

        public VoiceCharacter Owner
        {
            get;
        }

        public IReadOnlyList<ICharacter> Membership
        {
            get;
        }

        private VoiceCharacter Speaker
        {
            get;
        }

        private VoiceCharacter TwinA
        {
            get;
        }

        private VoiceCharacter TwinB
        {
            get;
        }

        public TestMind Mind
        {
            get;
        }

        public async Task ReadyAsync()
        {
            SceneTree sceneTree = TestUtils.GetSceneTree();
            (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(Mind);
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            ScenarioContext context = new(Owner, new TestSceneContext(Membership));
            IMainThreadDispatcher dispatcher = Game.Instance.GetRequiredService<IMainThreadDispatcher>();
            AgentToolSession sessionServices = new(context, Mind, HistoryRenderer: null, Clock);
            _speakFunction = _speechTool.CreateFunction(context, Mind, dispatcher, sessionServices);
            _waitFunction = _waitTool.CreateFunction(context, Mind, dispatcher, sessionServices);
            _historyFunction = _historyTool.CreateFunction(context, Mind, dispatcher, sessionServices);
        }

        public ValueTask<object?> InvokeSpeakAsync(string speech, CancellationToken cancellationToken)
            => InvokeAsync(_speakFunction!, new Dictionary<string, object?> { ["speech"] = speech }, cancellationToken);

        public ValueTask<object?> InvokeWaitAsync(float? seconds, CancellationToken cancellationToken)
        {
            Dictionary<string, object?> arguments = [];
            if (seconds is { } value)
            {
                arguments["seconds"] = value;
            }

            return InvokeAsync(_waitFunction!, arguments, cancellationToken);
        }

        /// <summary>Invokes the wait tool without a tree fixture, for the missing-clock failure contract.</summary>
        public ValueTask<object?> InvokeWaitReadylessAsync(CancellationToken cancellationToken)
        {
            ScenarioContext context = new(Owner, new TestSceneContext(Membership));
            IMainThreadDispatcher dispatcher = Game.Instance.GetRequiredService<IMainThreadDispatcher>();
            AgentToolSession sessionServices = new(context, Mind, HistoryRenderer: null, Clock: null);
            AIFunction waitFunction = _waitTool.CreateFunction(context, Mind, dispatcher, sessionServices);
            return InvokeAsync(waitFunction, new Dictionary<string, object?>(), cancellationToken);
        }

        public ValueTask<object?> InvokeHistoryAsync(int? count, CancellationToken cancellationToken)
        {
            Dictionary<string, object?> arguments = [];
            if (count is { } value)
            {
                arguments["count"] = value;
            }

            return InvokeAsync(_historyFunction!, arguments, cancellationToken);
        }

        public static async Task WaitForFramesAsync(int frameCount)
        {
            SceneTree sceneTree = TestUtils.GetSceneTree();
            await TestUtils.WaitForFramesAsync(sceneTree, frameCount);
        }

        public async ValueTask DisposeAsync()
        {
            SceneTree sceneTree = TestUtils.GetSceneTree();
            Mind.QueueFree();
            HandOffVoice?.Free();
            _speechTool.Free();
            _waitTool.Free();
            _historyTool.Free();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
        }

        private static ValueTask<object?> InvokeAsync(
            AIFunction function,
            IDictionary<string, object?> arguments,
            CancellationToken cancellationToken)
            => function.InvokeAsync(new AIFunctionArguments(arguments), cancellationToken);
    }

    private sealed partial class TestMind(VoiceCharacter owner) : MindBase
    {
        public void ObserveForTest(AgentObservation observation) => Observe(observation);

        public void AttendForTest(string fullId)
            => ReinforceAttention(fullId, 1f, AttentionSettings.Create(1f, 0f, 0.05f, 0.25f));

        public IReadOnlyList<AgentObservation> GetTimelineForTest() => GetObservationTimelineSnapshot();

        protected override ICharacter ResolveOwningCharacter() => owner;
    }

    /// <summary>
    /// Voice whose cancellable submission the test controls: submissions are recorded, the hand-off completes
    /// only on demand, and caller cancellation is observed.
    /// </summary>
    private sealed class ControllableVoice(string id) : IVoice
    {
        private readonly TaskCompletionSource _handOff = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id { get; set; } = id;

        public string Type => "voice";

        public string FullId => $"voice:{Id}";

        public bool IsSpeaking
        {
            get; private set;
        }

        public List<string> Submissions { get; } = [];

        public TaskCompletionSource SubmissionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CancellationObserved
        {
            get; private set;
        }

        public event Action<IVoice>? SpeechStarted;

        public event Action<IVoice>? SpeechEnded;

        public Vector3 Origin => Vector3.Zero;

        public void BeginSpeakingWindow()
        {
            IsSpeaking = true;
            SpeechStarted?.Invoke(this);
        }

        public void Speak(string speech)
        {
        }

        public ValueTask SpeakAsync(string speech, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public async ValueTask SpeakCancellableAsync(string speech, CancellationToken cancellationToken = default)
        {
            Submissions.Add(speech);
            IsSpeaking = true;
            SpeechStarted?.Invoke(this);
            _ = SubmissionStarted.TrySetResult();
            try
            {
                await _handOff.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                IsSpeaking = false;
                SpeechEnded?.Invoke(this);
                throw;
            }
        }

        public void CompleteHandOff() => _ = _handOff.TrySetResult();
    }

    /// <summary>Windowed voice whose speaking state and events are driven explicitly by the test.</summary>
    private sealed class WindowedVoice(string id) : IVoice
    {
        public string Id { get; set; } = id;

        public string Type => "voice";

        public string FullId => $"voice:{Id}";

        public bool IsSpeaking
        {
            get;
            private set;
        }

        public event Action<IVoice>? SpeechStarted;

        public event Action<IVoice>? SpeechEnded;

        public Vector3 Origin => Vector3.Zero;

        public void BeginSpeech()
        {
            IsSpeaking = true;
            SpeechStarted?.Invoke(this);
        }

        public void EndSpeech()
        {
            IsSpeaking = false;
            SpeechEnded?.Invoke(this);
        }

        public void Speak(string speech)
        {
        }

        public ValueTask SpeakAsync(string speech, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask SpeakCancellableAsync(string speech, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    /// <summary>
    /// <see cref="AIVoice" /> double whose hand-off the test completes on demand, so the shared cut capability
    /// can be observed after cancellation.
    /// </summary>
    private sealed partial class HandOffAIVoice : AIVoice
    {
        private readonly TaskCompletionSource _handOffCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HandOffAIVoice()
        {
            Id = "handoff-voice";
            SpeechEnded += OnSpeechEnded;
        }

        public TaskCompletionSource HandOffStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int SpeechEndedCount
        {
            get; private set;
        }

        public override ValueTask SpeakCancellableAsync(string speech, CancellationToken cancellationToken = default)
        {
            OpenSpeakingWindow();
            _ = HandOffStarted.TrySetResult();
            return new ValueTask(_handOffCompletion.Task);
        }

        public void CompleteHandOff() => _ = _handOffCompletion.TrySetResult();

        private void OnSpeechEnded(IVoice voice) => SpeechEndedCount++;
    }

    private sealed class VoiceCharacter(string id, IVoice voice) : ICharacter
    {
        public string Id { get; set; } = id;

        public string FullId => $"char:{Id}";

        public IReadOnlyList<IComponent> Components { get; } = [voice];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public IReadOnlyDictionary<string, object?> GetContext(ISceneContext scene, IContextual? observer)
            => new Dictionary<string, object?> { ["FullId"] = FullId };
    }

    private sealed class FakeGameClock : IGameClock
    {
        public double NowSeconds
        {
            get;
            set;
        }
    }

    private sealed record TestSceneContext(IReadOnlyCollection<ICharacter> Characters) : ISceneContext
    {
        public ICharacter Player => throw new InvalidOperationException(
            "Scene context contains no player character. Scene authoring guarantees the player is present.");

        public ContentContext Content => ContentContext.Default;

        public IIdentifiable? Find(string fullId)
            => Characters.FirstOrDefault(character => string.Equals(character.FullId, fullId, StringComparison.Ordinal));

        public IIdentifiable Resolve(string fullId)
            => Find(fullId) ?? throw new InvalidOperationException();
    }
}
