using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AlleyCat.Animations;
using AlleyCat.Character;
using AlleyCat.Core.Content;
using AlleyCat.IntegrationTests.Mind.AI.Decision;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.IntegrationTests.Support.AI;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Decision;
using AlleyCat.Mind.AI.Provider;
using AlleyCat.Mind.Attention;
using AlleyCat.Mind.Observation;
using AlleyCat.Navigation;
using AlleyCat.Navigation.Recording;
using AlleyCat.Scene;
using AlleyCat.Speech.Voice;
using AlleyCat.TestFramework;
using AlleyCat.Vision;
using Godot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using CharacterHub = AlleyCat.Character.Character;

namespace AlleyCat.IntegrationTests.Animations;

/// <summary>
/// ANIM-003-MIND-AC-004 live session (opt-in, <c>--live-llm</c>): NPC A, prompted in character by a fixed scenario
/// that names no tool, is expected to change its stance and gait through its own catalogue fragment, while NPC B,
/// focused on A, receives the description-only observations. One fact runs one independent trial; the outcome
/// criteria are fixed in <see cref="Evaluate" /> before any run, and every artefact is kept under
/// <c>game/temp/anim003-live/</c>.
/// </summary>
/// <remarks>
/// The NPCs have no movement tool, so the scenario scripts A's next movement after its gait choice; criterion 2 checks
/// that this movement starts in the chosen gait. The test-only alternate <c>slump</c> and <c>stroll</c> variants
/// give A a second stance and gait; production content is unchanged.
/// </remarks>
[LiveLlm]
public sealed class AnimationMindLiveSessionTests
{
    private const string NpcScene = "res://assets/testing/navigation/navigation_test_npc.tscn";
    private const string ScenarioPath = "res://tests/animation/catalogue/live/stance_gait_scenario.md";
    private const int RequestBudget = 12;

    private static readonly LiveLLMScenarioBounds _bounds = new(MaxTargetRequests: RequestBudget + 2, Timeout: TimeSpan.FromMinutes(7));

    // Criterion 4: tool or identifier meta talk in speech or free text.
    private static readonly Regex _metaTalk = new(@"change_stance|change_gait|\btools?\b|\bfunction\b|`[a-z_]+`|\bids?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>One live trial against criteria 1–4 (see <see cref="Evaluate" />).</summary>
    [Fact]
    public async Task StanceAndGait_LiveTrial()
    {
        IConfiguration configuration = Game.Instance.GetRequiredService<IConfiguration>();
        string runRoot = ProjectSettings.GlobalizePath($"res://temp/anim003-live/run-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}");
        _ = Directory.CreateDirectory(runRoot);
        // Allow-listed metadata only. Never serialise the configuration or credentials.
        File.WriteAllText(Path.Combine(runRoot, "metadata.json"), JsonSerializer.Serialize(new
        {
            model = configuration["AITest:Model"],
            hostSHA256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(configuration["AITest:Host"] ?? ""))),
            requestBudget = RequestBudget,
            timeout = _bounds.Timeout,
            scenarioSHA256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(ProjectSettings.GlobalizePath(ScenarioPath)))),
            startedUtc = DateTimeOffset.UtcNow,
        }, new JsonSerializerOptions { WriteIndented = true }));

        using LiveLLMClientPair clients = LiveLLMClientFactory.CreateLiveClients();
        LiveLLMScenarioExecutor executor = new(clients.Target);
        TrialEvidence? evidence = null;
        LiveLLMScenarioExecution execution = await executor.ExecuteAsync(
            "anim003-mind-ac-004", _bounds, async context => evidence = await RunTrialAsync(context, runRoot));
        _ = new LiveLLMTraceWriter(new DirectoryInfo(runRoot)).Write(execution.Trace);
        Assert.True(execution.Succeeded, execution.Failure is null ? "Execution failed." : $"{execution.Failure.Classification}: {execution.Failure.SafeMessage}");
        Assert.NotNull(evidence);

        Verdict verdict = Evaluate(evidence);
        File.WriteAllText(Path.Combine(runRoot, "evidence.md"), Describe(evidence, verdict));
        Assert.True(verdict.Passed, $"Trial failed: {verdict}. Evidence: {runRoot}");
    }

    private static async Task<TrialEvidence> RunTrialAsync(LiveLLMScenarioContext context, string runRoot)
    {
        SceneTree tree = TestUtils.GetSceneTree();
        DateTime deadline = DateTime.UtcNow + _bounds.Timeout - TimeSpan.FromSeconds(30);
        Node scene = GD.Load<PackedScene>(NavigationPlaytestRecorder.PlaytestScene).Instantiate();
        CharacterHub a = scene.GetNode<CharacterHub>("NavigationTestNpc");
        CharacterHub player = scene.GetNode<CharacterHub>("NavigationTestPlayer");
        CharacterHub b = Assert.IsType<CharacterHub>(GD.Load<PackedScene>(NpcScene).Instantiate(), exactMatch: false);
        b.Name = "ObserverNpc";
        b.Id = "navigation_test_observer";
        b.Position = new Vector3(0f, 0f, 3f);
        scene.AddChild(b);
        TestUtils.EnsureCharacterRuntimeInstalled(a);
        TestUtils.EnsureCharacterRuntimeInstalled(b);

        var catalogue = AnimationCatalogue.RequireFor(a);
        AnimationFixtureVariants.AddAlternates(catalogue);

        LiveClientProvider provider = new(context);
        AgenticMind mindA = a.GetNode<AgenticMind>("Mind");
        AgenticMind mindB = b.GetNode<AgenticMind>("Mind");
        SceneContext sceneContext = new([a, b, player], ContentContext.Default);
        mindA.ClientProvider = provider;
        mindA.ScenarioManager = new FixedScenarioManager { DescriptionPath = ScenarioPath };
        mindA.SetSceneDecisionClientForTesting(new AlwaysActWaitDecisionClient());
        mindA.WaitEvaluationIntervalSeconds = 0.05f;
        mindA.SetDiagnosticsSettingsLoaderForTesting(() => new AIDiagnosticsSettings(
            EnableRequestResponseLogging: false, EnableReasoningLogging: true, EnableSessionTranscriptLogging: true));
        mindA.SetSceneContextLoaderForTesting(() => sceneContext);
        mindA.SetVisualContextModeLoaderForTesting(static () => VisualContextMode.Tools);
        // B only observes: its session holds its first request forever and never reaches a provider.
        mindB.ClientProvider = new HoldingClientProvider();
        mindB.SetSceneDecisionClientForTesting(new AlwaysActWaitDecisionClient());
        mindB.SetSceneContextLoaderForTesting(() => sceneContext);
        mindB.SetVisualContextModeLoaderForTesting(static () => VisualContextMode.Off);
        // B's focus is held on A by the scenario, not by its gaze selector.
        mindB.GetNode<AttentionGazeTargetSelector>("AttentionGazeTargetSelector").ProcessMode = Node.ProcessModeEnum.Disabled;

        List<AnimationTransition> transitions = [];
        MindSessionTranscriptRecorder.ResetTurnCountersForTesting();
        MindSessionTranscriptRecorder.SetRootPathOverrideForTesting(Path.Combine(runRoot, "transcripts"));
        Node root = tree.CurrentScene ?? tree.Root;
        root.AddChild(scene);
        try
        {
            await WaitUntilAsync(tree, () => a.HasComponentProjection && a.Voice is AIVoice
                && a.Navigation is MotionBricksNavigation { NativeIdleOwnsBody: true }, deadline, "A's installation");
            MotionBricksNavigation navigation = Assert.IsType<MotionBricksNavigation>(a.Navigation);
            Assert.Equal("", navigation.Failure);
            navigation.TransitionStarted += transitions.Add;
            ReplaceVoice(a);
            VisualCue cue = Assert.IsType<VisualCue>(a.VisualCues.FirstOrDefault(), exactMatch: false);
            EyesBehaviour eyes = Assert.IsType<EyesBehaviour>(b.Eyes);
            eyes.SetLookTarget(cue);
            int focusReasserts = 0;
            provider.Release();

            // Run until A has chosen both a stance and a gait, or the request budget is spent.
            while (DateTime.UtcNow < deadline
                && provider.Completed < RequestBudget
                && (catalogue.Idle.Preference is null || catalogue.Locomotion.Preference is null))
            {
                if (!ReferenceEquals(eyes.LookTarget, cue))
                {
                    eyes.SetLookTarget(cue);
                    focusReasserts++;
                }

                await TestUtils.WaitForNextFrameAsync(tree);
            }

            provider.Stop();
            string? stance = catalogue.Idle.Preference;
            string? gait = catalogue.Locomotion.Preference;
            if (stance is not null)
            {
                await WaitUntilAsync(tree, () => navigation.NativeIdleOwnsBody && navigation.Phase == "settled-idle"
                    && transitions.Any(t => t.Category == AnimationCategory.Idle && t.Entry.ID == stance), deadline, "the stance bridge", required: false);
            }

            bool moved = false;
            if (gait is not null)
            {
                // Scripted next movement (the NPC has no movement tool): the gait must apply to it.
                var destination = new Transform3D(Basis.Identity, a.GlobalPosition + new Vector3(2f, 0f, -1.5f));
                moved = scene.Call("_submit_destination", destination, Array.Empty<Vector3>()).AsInt32() == 0;
                await WaitUntilAsync(tree, () => transitions.Any(t => t.Kind == AnimationTransitionKind.IdleToLocomotion),
                    deadline, "the departure", required: false);
            }

            await TestUtils.WaitForFramesAsync(tree, 10);
            await mindB.DrainPerceptionsForTestingAsync();
            return new TrialEvidence(
                provider.Snapshot(),
                [.. transitions],
                [.. mindB.GetObservationTimelineSnapshot().OfType<ObservedAnimationChange>()],
                ((ICharacter)a).FullId,
                stance,
                gait,
                moved,
                focusReasserts,
                [.. catalogue.Idle.Entries.Concat(catalogue.Locomotion.Entries)],
                b);
        }
        finally
        {
            provider.Stop();
            MindSessionTranscriptRecorder.SetRootPathOverrideForTesting(null);
            if (GodotObject.IsInstanceValid(scene))
            {
                root.RemoveChild(scene);
                scene.QueueFree();
            }

            await TestUtils.WaitForFramesAsync(tree, 4);
        }
    }

    // The outcome criteria, fixed before any run.
    private static Verdict Evaluate(TrialEvidence e)
    {
        IReadOnlyList<FunctionCallContent> calls = [.. e.Requests.SelectMany(static r => r.Response.Messages)
            .SelectMany(static m => m.Contents).OfType<FunctionCallContent>()];
        bool ValidCall(string tool, AnimationCategory category, out string? id)
        {
            id = calls.Where(c => c.Name == tool)
                .Select(static c => c.Arguments?.TryGetValue("id", out object? v) == true ? v?.ToString()?.Trim('`', ' ') : null)
                .LastOrDefault(v => v is not null && e.Entries.Any(entry => entry.Category == category && entry.ID == v));
            return id is not null;
        }

        // 1. change_stance with a valid id, and a transition to that idle starts.
        bool c1 = ValidCall("change_stance", AnimationCategory.Idle, out string? stanceId)
            && e.Transitions.Any(t => t.Category == AnimationCategory.Idle && t.Entry.ID == stanceId);
        // 2. change_gait with a valid id, and the next movement starts in that gait.
        bool c2 = ValidCall("change_gait", AnimationCategory.Locomotion, out string? gaitId) && e.Moved
            && e.Transitions.FirstOrDefault(t => t.Kind == AnimationTransitionKind.IdleToLocomotion)?.Entry.ID == gaitId;
        // 3. B received the description-only observations for the chosen stance and gait, with no id or tags rendered.
        bool Observed(string? id, AnimationCategory category)
        {
            return id is not null && e.BObservations.Any(o => o.SubjectId == e.SubjectId && o.Category == category
                        && o.Description == e.Entries.Single(x => x.Category == category && x.ID == id).Description);
        }

        bool clean = e.BObservations.All(o =>
        {
            string rendered = o.Render(e.Observer);
            return e.Entries.All(entry => !rendered.Contains($"`{entry.ID}`", StringComparison.Ordinal)
                && entry.Tags.All(tag => !Regex.IsMatch(rendered.Replace(entry.Description, "", StringComparison.Ordinal), $@"\b{Regex.Escape(tag)}\b")));
        });
        bool c3 = Observed(stanceId, AnimationCategory.Idle) && Observed(gaitId, AnimationCategory.Locomotion) && clean;
        // 4. No tool or id meta talk in speech or free text.
        IReadOnlyList<string> speech = SpeechAndText(e);
        bool c4 = speech.All(text => !_metaTalk.IsMatch(text));
        return new Verdict(c1, c2, c3, c4, stanceId, gaitId);
    }

    private static List<string> SpeechAndText(TrialEvidence e)
    {
        List<string> texts = [];
        foreach (ChatMessage message in e.Requests.SelectMany(static r => r.Response.Messages))
        {
            if (!string.IsNullOrWhiteSpace(message.Text))
            {
                texts.Add(message.Text);
            }

            foreach (FunctionCallContent call in message.Contents.OfType<FunctionCallContent>().Where(static c => c.Name == "speak"))
            {
                texts.AddRange(call.Arguments?.Values.OfType<object>().Select(static v => v.ToString() ?? "") ?? []);
            }
        }

        return texts;
    }

    private static string Describe(TrialEvidence e, Verdict v)
    {
        StringBuilder text = new();
        _ = text.AppendLine($"# MIND-AC-004 Live Trial\n\nVerdict: {v}\n\nStance preference: {e.Stance ?? "-"}; gait preference: {e.Gait ?? "-"}; moved: {e.Moved}; B focus re-asserts: {e.FocusReasserts}\n");
        _ = text.AppendLine("## A Tool Calls And Text\n");
        int index = 0;
        foreach (CapturedRequest request in e.Requests)
        {
            _ = text.AppendLine($"### Request {index++}\n");
            foreach (FunctionResultContent result in request.Messages.SelectMany(static m => m.Contents).OfType<FunctionResultContent>().TakeLast(3))
            {
                _ = text.AppendLine($"- result `{result.CallId}`: {result.Result}");
            }

            foreach (ChatMessage message in request.Response.Messages)
            {
                if (!string.IsNullOrWhiteSpace(message.Text))
                {
                    _ = text.AppendLine($"- text: {message.Text}");
                }

                foreach (FunctionCallContent call in message.Contents.OfType<FunctionCallContent>())
                {
                    _ = text.AppendLine($"- call {call.Name}({JsonSerializer.Serialize(call.Arguments)})");
                }
            }

            _ = text.AppendLine();
        }

        _ = text.AppendLine("## A Transitions\n");
        foreach (AnimationTransition t in e.Transitions)
        {
            _ = text.AppendLine($"- {t.Kind} → {t.Category.ToName()}/{t.Entry.ID}");
        }

        _ = text.AppendLine("\n## B Observations (Rendered)\n");
        foreach (ObservedAnimationChange o in e.BObservations)
        {
            _ = text.AppendLine($"- [{o.Category.ToName()}] {o.Render(e.Observer)}");
        }

        return text.ToString();
    }

    private static async Task WaitUntilAsync(SceneTree tree, Func<bool> condition, DateTime deadline, string what, bool required = true)
    {
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                if (required)
                {
                    throw new TimeoutException($"Timed out waiting for {what}.");
                }

                return;
            }

            await TestUtils.WaitForNextFrameAsync(tree);
        }
    }

    private static void ReplaceVoice(CharacterHub character)
    {
        var authored = (Node)character.Voice!;
        Node parent = authored.GetParent();
        int index = authored.GetIndex();
        authored.Free();
        DeterministicVoice replacement = new()
        {
            Name = "Voice",
            Id = character.Id
        };
        parent.AddChild(replacement);
        parent.MoveChild(replacement, index);
        character.Voice = replacement;
        character.RefreshComponents();
    }

    private sealed record TrialEvidence(
        IReadOnlyList<CapturedRequest> Requests,
        IReadOnlyList<AnimationTransition> Transitions,
        IReadOnlyList<ObservedAnimationChange> BObservations,
        string SubjectId,
        string? Stance,
        string? Gait,
        bool Moved,
        int FocusReasserts,
        IReadOnlyList<AnimationEntry> Entries,
        ICharacter Observer);

    private sealed record Verdict(bool C1, bool C2, bool C3, bool C4, string? StanceId, string? GaitId)
    {
        public bool Passed => C1 && C2 && C3 && C4;
    }

    private sealed record CapturedRequest(IReadOnlyList<ChatMessage> Messages, ChatResponse Response);

    /// <summary>Deterministic publishing voice: real group publication without audio generation.</summary>
    private sealed partial class DeterministicVoice : Voice;

    /// <summary>Forwards A's requests to the traced live target; after <see cref="Stop" />, requests hold until teardown.</summary>
    private sealed partial class LiveClientProvider(LiveLLMScenarioContext context) : ClientProvider
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<CapturedRequest> _requests = [];
        private readonly Lock _gate = new();
        private volatile bool _stopped;

        public int Completed
        {
            get
            {
                lock (_gate)
                {
                    return _requests.Count;
                }
            }
        }

        public void Release() => _released.TrySetResult();

        public void Stop()
        {
            _stopped = true;
            _ = _released.TrySetResult();
        }

        public IReadOnlyList<CapturedRequest> Snapshot()
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }

        public override IChatClient CreateChatClient(string subjectFullId) => new Client(this);

        private sealed class Client(LiveClientProvider owner) : IChatClient
        {
            public async Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            {
                IReadOnlyList<ChatMessage> request = [.. messages];
                await owner._released.Task.WaitAsync(cancellationToken);
                if (owner._stopped)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                ChatResponse response = await owner.SendAsync(request, options, cancellationToken);
                return response;
            }

            public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
                => throw new NotSupportedException();

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose()
            {
            }
        }

        private async Task<ChatResponse> SendAsync(IReadOnlyList<ChatMessage> request, ChatOptions? options, CancellationToken cancellationToken)
        {
            ChatResponse response = await context.SendTargetAsync(request, options, cancellationToken);
            lock (_gate)
            {
                _requests.Add(new CapturedRequest(request, response));
            }

            return response;
        }
    }

    /// <summary>Holds every request until cancellation; B never reaches a provider.</summary>
    private sealed partial class HoldingClientProvider : ClientProvider
    {
        public override IChatClient CreateChatClient(string subjectFullId) => new Client();

        private sealed class Client : IChatClient
        {
            public async Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new ChatResponse();
            }

            public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
                => throw new NotSupportedException();

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose()
            {
            }
        }
    }

    private sealed class AlwaysActWaitDecisionClient : IDecisionClient
    {
        public Task<DecisionResult> EvaluateAsync(DecisionRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ScriptedWaitVerdicts.Answer(request, WaitVerdict.ActNow));
        }
    }
}
