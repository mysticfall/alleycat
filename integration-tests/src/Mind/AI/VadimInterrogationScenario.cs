using System.Globalization;
using System.Text.RegularExpressions;
using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Core.Content;
using AlleyCat.Core.Time;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.IntegrationTests.Support.AI;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Provider;
using AlleyCat.Scene;
using AlleyCat.Speech;
using AlleyCat.Speech.Voice;
using AlleyCat.Vision;
using Godot;
using Microsoft.Extensions.AI;
using Xunit;
using CharacterHub = AlleyCat.Character.Character;

namespace AlleyCat.IntegrationTests.Mind.AI;

/// <summary>
/// Fixed facts of the Vadim interrogation baseline scenario: the representative reproduction of the reported
/// no-reply symptom on the authored interrogation scene, with the corrected name-question precondition,
/// Hearing-path delivery, and ~43s game-time chronology.
/// </summary>
/// <remarks>
/// <para>
/// Baseline (old pipeline, <c>qwen3.8-27b</c>, turns 1–6): Vadim opens the interrogation by asking for the player's
/// full name, the scripted player line <c>My name is Ellie Onyx</c> from <c>char:ally</c> is delivered at ~43s
/// game time, and the model never replies — turns 3–5 answer with <c>wait</c> despite the delivered line, and its
/// one engaged response (turn 6) is discarded as invalid. This scenario reproduces the preconditions under the
/// Option B pipeline using the authored Vadim NPC scene, its authored Mind, prompt stack, interrogation lore, and
/// tool inventory.
/// </para>
/// <para>
/// Corrected chronology (plan: Corrected Experiment Design): the scripted line is published through a real voice
/// node's group publication into the authored scene's Hearing sense, committed by the authored SpeechPerception
/// with facing-cone addressee inference, and delivered as a runtime-authored synthetic scene update at
/// approximately 43s of scenario game time — after the accepted, settled full-name question. The opening gate
/// classifies only the accepted, settled opening speech and fails closed: without an explicit full-name question
/// the scenario stops without delivering the name and the trial slot is consumed.
/// </para>
/// </remarks>
internal static class VadimInterrogationBaseline
{
    /// <summary>Stable scenario name recorded in traces and used for artefact file names.</summary>
    public const string ScenarioName = "vadim-interrogation-baseline";

    /// <summary>Godot resource path of the authored Vadim NPC scene driving the representative path.</summary>
    public const string VadimNPCScenePath = "res://assets/characters/reference/vadim_npc.tscn";

    /// <summary>The owning NPC's full ID, matching the baseline character.</summary>
    public const string OwnerFullId = "char:vadim";

    /// <summary>The player character's full ID; her identity is unknown to Vadim until spoken.</summary>
    public const string PlayerFullId = "char:ally";

    /// <summary>The scripted player line whose non-reply is the parent symptom.</summary>
    public const string PlayerLine = "My name is Ellie Onyx";

    /// <summary>
    /// The rendered observation line the synthetic scene-update exchange delivers for the player line, without
    /// the trailing game-time suffix. The renderer appends the game time directly, so the spoken sentence
    /// carries no trailing period here. The <c>say to me</c> wording proves the authored addressee inference
    /// resolved Vadim as the addressee of the published speech.
    /// </summary>
    public const string PlayerLineDeliveryPrefix = "Heard `char:ally` say to me: My name is Ellie Onyx";

    /// <summary>Number of requests after the delivery request that complete the observation window.</summary>
    public const int WindowFollowUpRequests = 2;

    /// <summary>
    /// Game time, in seconds on the scenario's session clock, at which the scripted player line is published:
    /// the baseline delivered it at ~43.4s of game time, and this target preserves that model-visible anchor.
    /// </summary>
    public const double DeliveryTargetGameTimeSeconds = 43d;

    /// <summary>
    /// Minimum game-time gap, in seconds, between the settled opening question and the delivery for the
    /// precondition to hold: an opening that settles too late for representative delivery stops the trial.
    /// </summary>
    public const double MinimumOpeningToDeliveryGapSeconds = 1d;

    /// <summary>Bounds for the deterministic smoke run: scripted turns plus recovery and teardown slack.</summary>
    public static LiveLLMScenarioBounds SmokeBounds => new(
        MaxTargetRequests: 8,
        Timeout: TimeSpan.FromSeconds(150));

    /// <summary>
    /// Bounds for the live run: the unchanged declared ceilings — the baseline's ~6 turns plus invalid-response
    /// recovery retries and the request the scenario's node exit cancels mid-flight, within the prior
    /// eight-minute scenario budget.
    /// </summary>
    public static LiveLLMScenarioBounds LiveBounds => new(
        MaxTargetRequests: 12,
        Timeout: TimeSpan.FromMinutes(8));
}

/// <summary>One completed provider request as the scenario observed it: the exact message list sent and the raw
/// response that settled it, before any runner validation.</summary>
internal sealed record VadimInterrogationRequest(
    int Index,
    IReadOnlyList<ChatMessage> Messages,
    ChatResponse Response,
    int SettledTurnsAtRequestStart);

/// <summary>
/// Evidence summary of one scenario execution: the completed requests in arrival order, the zero-based index of
/// the delivery request, the transcript recorder's turn files, and the opening-gate decision — the inputs the
/// parent contract's acceptance criteria judge over.
/// </summary>
/// <param name="CompletedRequests"></param>
/// <param name="DeliveryRequestIndex"></param>
/// <param name="TurnFiles"></param>
/// <param name="PreconditionSatisfied">Whether the accepted, settled opening satisfied the full-name-question
/// precondition in time for representative delivery.</param>
/// <param name="OpeningKind">The fail-closed gate classification of the opening speech content.</param>
/// <param name="OpeningAcceptedSpeech">The joined accepted opening <c>speak</c> content, when any settled.</param>
/// <param name="OpeningSettledGameTimeSeconds">Scenario session-clock game time observed when the opening turn
/// page — its settlement proof — appeared.</param>
internal sealed record VadimInterrogationResult(
    IReadOnlyList<VadimInterrogationRequest> CompletedRequests,
    int DeliveryRequestIndex,
    IReadOnlyList<string> TurnFiles,
    bool PreconditionSatisfied,
    VadimOpeningKind OpeningKind,
    string? OpeningAcceptedSpeech,
    double OpeningSettledGameTimeSeconds)
{
    /// <summary>Whether the full observation window completed: delivery plus the configured follow-ups.</summary>
    public bool WindowCompleted
        => DeliveryRequestIndex >= 0
            && CompletedRequests.Count
                >= DeliveryRequestIndex + 1 + VadimInterrogationBaseline.WindowFollowUpRequests;
}

/// <summary>
/// Drives the representative Vadim interrogation scenario inside a <see cref="LiveLLMScenarioContext" />: it loads
/// the authored Vadim NPC scene, wires its authored mind to the executor's traced target client, waits for the
/// accepted full-name-question precondition, publishes the scripted player line through the authored
/// speech-perception/Hearing path at the baseline's ~43s game time, and ends the session once the observation
/// window completes.
/// </summary>
/// <remarks>
/// <para>
/// The scenario owns no clients and contacts no network: the executor's borrowed target client — live or the
/// deterministic scripted fake — is the only transport, so the smoke run exercises the identical plumbing the
/// live run uses. Turn-level evidence (accepted/discarded outcomes with reasons, stripped-text records, full
/// request transcripts) is captured by the real AI-011 transcript recorder, whose files the scenario collects.
/// </para>
/// <para>
/// Substituted boundaries are deliberate and recorded in the experiment evidence: the authored AIVoice's TTS
/// generation, lip-sync, and audible playback are replaced at the authored voice node by a deterministic
/// publishing voice (the runner's playback hand-off and the group publication path still run); the fixture player
/// publishes through a real <see cref="Voice" /> node rather than an audio pipeline; and the scenario session
/// clock is re-baselined through the mind's internal test seam so the delivery carries the baseline's ~43s
/// game-time stamp while real time still passes one second per game second between question and delivery.
/// </para>
/// </remarks>
internal sealed partial class VadimInterrogationScenario
{
    private const string OwnerId = "vadim";
    private const string PlayerId = "ally";

    private readonly string _transcriptRoot;
    private readonly TimeSpan _timeout;
    private readonly double _deliveryTargetGameTimeSeconds;

    public VadimInterrogationScenario(
        string transcriptRoot,
        TimeSpan? timeout = null,
        double? deliveryTargetGameTimeSeconds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transcriptRoot);
        _transcriptRoot = transcriptRoot;
        _timeout = timeout ?? VadimInterrogationBaseline.LiveBounds.Timeout;
        _deliveryTargetGameTimeSeconds = deliveryTargetGameTimeSeconds
            ?? VadimInterrogationBaseline.DeliveryTargetGameTimeSeconds;
    }

    /// <summary>Runs the scenario to the end of its observation window and returns the captured evidence.</summary>
    /// <param name="context">The executor scenario context whose target client receives every provider roundtrip.</param>
    /// <param name="cancellationToken">Token cancelling the whole execution.</param>
    /// <returns>The scenario's evidence summary, including the opening-gate decision.</returns>
    public async Task<VadimInterrogationResult> RunAsync(
        LiveLLMScenarioContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        DateTime deadline = DateTime.UtcNow + _timeout;

        SceneTree sceneTree = TestUtils.GetSceneTree();
        // A fresh session clock re-baselines game time to scenario start through the mind's internal test seam, so
        // the delivery lands at the baseline's model-visible ~43s stamp; the clock still advances one second per
        // second of real time.
        GameClock scenarioClock = new();

        InterrogationSessionClientProvider clientProvider = new()
        {
            Context = context,
            SettledTurnCount = () => ListTurnFiles().Count,
        };

        Node vadimNode = TestUtils.LoadPackedScene(VadimInterrogationBaseline.VadimNPCScenePath).Instantiate();
        CharacterHub vadim = Assert.IsType<CharacterHub>(vadimNode, exactMatch: false);
        AgenticMind mind = Assert.IsType<AgenticMind>(vadimNode.GetNode("Mind"), exactMatch: false);

        // Substituted audio boundary (documented in the experiment evidence): the authored AIVoice's TTS
        // generation, lip-sync, and audible playback are replaced at the authored voice node by a deterministic
        // publishing voice, keeping the runner's playback hand-off and the group publication path. The swap
        // happens after the scene's deferred rig installation has finished, and the first provider request waits
        // for it, so the opening speak executes against the final wiring.
        DeterministicVoice allyVoice = new()
        {
            Name = "AllyVoice",
            Id = PlayerId
        };
        InterrogationPlayerCharacter player = new(allyVoice)
        {
            Id = PlayerId,
            // Seated two metres in front of Vadim, body forward toward him: the authored facing-cone addressee
            // inference resolves Vadim as the addressee of her published speech through her orientation.
            Position = new Vector3(0f, 0f, 2f),
        };

        mind.ClientProvider = clientProvider;
        mind.SetDiagnosticsSettingsLoaderForTesting(() => new AIDiagnosticsSettings(
            EnableRequestResponseLogging: false,
            EnableReasoningLogging: true,
            EnableSessionTranscriptLogging: true));
        mind.SetSceneContextLoaderForTesting(
            () => new SceneContext([vadim, player], ContentContext.Default));
        mind.SetGameClockLoaderForTesting(() => scenarioClock);

        MindSessionTranscriptRecorder.ResetTurnCountersForTesting();
        MindSessionTranscriptRecorder.SetRootPathOverrideForTesting(_transcriptRoot);
        try
        {
            await WireAuthoredSceneAsync(
                sceneTree, vadim, vadimNode, player, allyVoice, clientProvider, deadline, cancellationToken);

            // Opening: the first request completes and its transcript page — the settlement proof — appears.
            await WaitUntilAsync(
                sceneTree,
                () => clientProvider.Snapshot().Count >= 1,
                "the opening turn to complete",
                deadline,
                cancellationToken);
            await WaitUntilAsync(
                sceneTree,
                () => ListTurnFiles().Count >= 1,
                "the opening turn's transcript page to appear",
                deadline,
                cancellationToken);

            (bool openingSettled, string? openingSpeech) = ReadOpeningSettlement(clientProvider, ListTurnFiles());
            double openingSettledGameTime = scenarioClock.NowSeconds;
            VadimOpeningKind openingKind = VadimOpeningGate.Classify(openingSettled ? openingSpeech : null);
            bool preconditionSatisfied = openingKind == VadimOpeningKind.FullNameQuestion
                && openingSettledGameTime
                    <= _deliveryTargetGameTimeSeconds - VadimInterrogationBaseline.MinimumOpeningToDeliveryGapSeconds;

            if (!preconditionSatisfied)
            {
                // Precondition missing: stop without publishing the player line; the trial slot is consumed and
                // no request or turn page ever carries the name.
                await TeardownAsync(sceneTree, vadimNode, player, allyVoice);
                return new VadimInterrogationResult(
                    clientProvider.Snapshot(),
                    -1,
                    ListTurnFiles(),
                    PreconditionSatisfied: false,
                    openingKind,
                    openingSpeech,
                    openingSettledGameTime);
            }

            // The follow-up request is in flight while the line lands: the fresh addressed-speech observation
            // invalidates it, and its replacement carries the synthetic scene-update exchange delivering the line.
            await WaitUntilAsync(
                sceneTree,
                () => clientProvider.StartedRequestCount >= 2,
                "the follow-up request to start",
                deadline,
                cancellationToken);

            // The baseline's model-visible delivery anchor: ~43s of game time after scenario start, with the
            // settled full-name question already retained in context.
            await WaitForGameTimeAsync(
                sceneTree,
                scenarioClock,
                _deliveryTargetGameTimeSeconds,
                $"the delivery game time ({_deliveryTargetGameTimeSeconds.ToString("F1", CultureInfo.InvariantCulture)}s)",
                deadline,
                cancellationToken);

            // Publish the scripted line through the real voice-publication path: the voice node's group
            // publication reaches the authored Hearing sense, whose SpeechPerception commits the addressed
            // observation into Mind — never a direct observation injection.
            await allyVoice.SpeakAsync(VadimInterrogationBaseline.PlayerLine, cancellationToken);

            await WaitUntilAsync(
                sceneTree,
                () => FindDeliveryRequestIndex(clientProvider) >= 0,
                "the player line to become model-visible",
                deadline,
                cancellationToken);
            await WaitUntilAsync(
                sceneTree,
                () => WindowComplete(clientProvider),
                "the observation window to complete",
                deadline,
                cancellationToken);
            // Let the closing turn's tool batch settle before ending the session: its transcript page appearing
            // proves the cycle resolved, so the page records its settled outcome rather than an interruption.
            int settledCyclesBeforeClosing = clientProvider.Snapshot()[^1].SettledTurnsAtRequestStart;
            await WaitUntilAsync(
                sceneTree,
                () => ListTurnFiles().Count > settledCyclesBeforeClosing,
                "the closing turn's transcript page to appear",
                deadline,
                cancellationToken);

            // The window is complete: wait for the post-window request to start, then end the session by node
            // exit. Its mid-flight cancellation is the contained quiet exit, never a scenario failure.
            int completedBeforeTeardown = clientProvider.Snapshot().Count;
            await WaitUntilAsync(
                sceneTree,
                () => clientProvider.StartedRequestCount >= completedBeforeTeardown + 1,
                "the post-window request to start",
                deadline,
                cancellationToken);
            await TeardownAsync(sceneTree, vadimNode, player, allyVoice);

            return new VadimInterrogationResult(
                clientProvider.Snapshot(),
                FindDeliveryRequestIndex(clientProvider),
                ListTurnFiles(),
                PreconditionSatisfied: true,
                openingKind,
                openingSpeech,
                openingSettledGameTime);
        }
        finally
        {
            DetachAndFree(sceneTree, vadimNode, player, allyVoice);
            MindSessionTranscriptRecorder.SetRootPathOverrideForTesting(null);
        }
    }

    /// <summary>
    /// Finds the zero-based index of the first completed request whose messages carry the synthetic
    /// scene-update exchange delivering the scripted player line: the request in which the line becomes
    /// model-visible.
    /// </summary>
    internal static int FindDeliveryRequestIndex(InterrogationSessionClientProvider provider)
    {
        IReadOnlyList<VadimInterrogationRequest> requests = provider.Snapshot();
        for (int index = 0; index < requests.Count; index++)
        {
            if (DeliversPlayerLine(requests[index].Messages))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Whether a request's messages carry a runtime-authored <c>scene_update</c> exchange whose result delivers
    /// the scripted player line (AI-002 TR-18).
    /// </summary>
    internal static bool DeliversPlayerLine(IReadOnlyList<ChatMessage> messages)
    {
        HashSet<string> deliveryCallIds =
        [
            .. messages
                .SelectMany(static message => message.Contents)
                .OfType<FunctionCallContent>()
                .Where(static call => call.Name == SyntheticSceneUpdateExchange.DeliveryToolName)
                .Select(static call => call.CallId),
        ];
        return messages
            .SelectMany(static message => message.Contents)
            .OfType<FunctionResultContent>()
            .Where(result => deliveryCallIds.Contains(result.CallId))
            .Any(result => result.Result?.ToString()?.Contains(
                VadimInterrogationBaseline.PlayerLineDeliveryPrefix,
                StringComparison.Ordinal) == true);
    }

    /// <summary>
    /// Reads the delivered line's model-visible game time — the <c>(at 43.4s game time)</c> suffix of the
    /// synthetic scene-update delivery — from a request's messages.
    /// </summary>
    internal static double? TryReadDeliveredLineGameTimeSeconds(IReadOnlyList<ChatMessage> messages)
    {
        string? delivery = messages
            .SelectMany(static message => message.Contents)
            .OfType<FunctionResultContent>()
            .Select(static content => content.Result?.ToString() ?? string.Empty)
            .FirstOrDefault(text => text.Contains(
                VadimInterrogationBaseline.PlayerLineDeliveryPrefix,
                StringComparison.Ordinal));
        if (delivery is null)
        {
            return null;
        }

        Match match = DeliveredGameTimeSuffix().Match(delivery);
        return match.Success
            && double.TryParse(match.Groups[1].ValueSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
            ? seconds
            : null;
    }

    [GeneratedRegex(@"\(at (\d+(?:\.\d+)?)s game time\)", RegexOptions.CultureInvariant)]
    private static partial Regex DeliveredGameTimeSuffix();

    /// <summary>
    /// Reads the opening settlement: the precondition holds only when the first turn was accepted AND its speak
    /// settled through the voice hand-off — the production speech-settlement result — never on an action failure,
    /// private reasoning, or assumed instruction.
    /// </summary>
    private static (bool AcceptedAndSettled, string? AcceptedSpeech) ReadOpeningSettlement(
        InterrogationSessionClientProvider provider,
        IReadOnlyList<string> turnFiles)
    {
        VadimInterrogationRequest opening = provider.Snapshot()[0];
        string? speech = JoinAcceptedSpeech(opening.Response);
        string openingPage = File.ReadAllText(turnFiles[0]);
        string outcome = openingPage
            .Split('\n')
            .FirstOrDefault(static line => line.StartsWith("- **Outcome:**", StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                "The opening transcript page carries no outcome line, so its settlement cannot be verified.");
        bool accepted = string.Equals(outcome, "- **Outcome:** Accepted", StringComparison.Ordinal);
        bool settledThroughVoice = openingPage.Contains(
            "Spoken through the configured voice.",
            StringComparison.Ordinal);
        bool acceptedAndSettled = accepted && settledThroughVoice;
        return (acceptedAndSettled, acceptedAndSettled ? speech : null);
    }

    private static string? JoinAcceptedSpeech(ChatResponse response)
    {
        string joined = string.Join(
            " ",
            response.Messages
                .SelectMany(static message => message.Contents)
                .OfType<FunctionCallContent>()
                .Where(static call => call.Name == "speak")
                .Select(call => call.Arguments is not null
                    && call.Arguments.TryGetValue("speech", out object? speech)
                        ? speech?.ToString()
                        : null)
                .Where(static text => !string.IsNullOrWhiteSpace(text))
                .Select(static text => text!.Trim()));
        return joined.Length > 0 ? joined : null;
    }

    private static bool WindowComplete(InterrogationSessionClientProvider provider)
    {
        int deliveryIndex = FindDeliveryRequestIndex(provider);
        return deliveryIndex >= 0
            && provider.Snapshot().Count
                >= deliveryIndex + 1 + VadimInterrogationBaseline.WindowFollowUpRequests;
    }

    private IReadOnlyList<string> ListTurnFiles()
    {
        // The recorder nests turn pages under a sortable run directory, then the character directory.
        return Directory.Exists(_transcriptRoot)
            ? [.. Directory
                .GetFiles(_transcriptRoot, "turn-*.md", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)]
            : [];
    }

    /// <summary>Replaces the authored AIVoice node at its authored path with a deterministic publishing voice.</summary>
    internal static void ReplaceAuthoredVoice(CharacterHub vadim, Node vadimNode)
    {
        Node authored = vadimNode.GetNode("Male/GeneralSkeleton/Head/Voice");
        Node parent = authored.GetParent();
        int index = authored.GetIndex();
        authored.Free();

        DeterministicVoice replacement = new()
        {
            Name = "Voice",
            Id = OwnerId
        };
        parent.AddChild(replacement);
        parent.MoveChild(replacement, index);
        vadim.Voice = replacement;
    }

    /// <summary>
    /// Adds the authored scene to the tree, waits for its deferred rig installation to commit the template-authored
    /// capability references, then swaps the authored AIVoice for the deterministic publishing voice and refreshes
    /// the component projection — and only then releases the first provider request, so the opening speak executes
    /// against the final wiring rather than racing the installer.
    /// </summary>
    private static async Task WireAuthoredSceneAsync(
        SceneTree sceneTree,
        CharacterHub vadim,
        Node vadimNode,
        Node player,
        Node allyVoice,
        InterrogationSessionClientProvider clientProvider,
        DateTime deadline,
        CancellationToken cancellationToken)
    {
        TaskCompletionSource componentProjectionCommitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnComponentProjectionCommitted()
        {
            _ = componentProjectionCommitted.TrySetResult();
        }

        vadim.ComponentsRefreshed += OnComponentProjectionCommitted;
        Node root = sceneTree.CurrentScene ?? sceneTree.Root;
        root.AddChild(player);
        root.AddChild(allyVoice);
        root.AddChild(vadimNode);
        try
        {
            while (!componentProjectionCommitted.Task.IsCompleted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException(
                        "The interrogation scenario timed out waiting for the authored scene's component projection.");
                }

                await TestUtils.WaitForNextFrameAsync(sceneTree);
            }

            _ = Assert.IsType<Hearing>(vadim.Hearing);

            // The deferred rig installation commits the template-authored capability references — including the
            // authored AIVoice — before this state holds, so the voice swap below cannot be raced by a reinstall.
            await WaitUntilAsync(
                sceneTree,
                () => vadim.HasComponentProjection && vadim.Voice is AIVoice,
                "the authored scene's installed AIVoice to be projected",
                deadline,
                cancellationToken);

            ReplaceAuthoredVoice(vadim, vadimNode);
            vadim.RefreshComponents();
            DeterministicVoice replacement = Assert.IsType<DeterministicVoice>(vadim.Voice);
            Assert.Contains(vadim.Components, component => ReferenceEquals(component, replacement));
            _ = Assert.Single(vadim.Components.OfType<IVoice>());
        }
        finally
        {
            vadim.ComponentsRefreshed -= OnComponentProjectionCommitted;
            clientProvider.SignalWiringComplete();
        }
    }

    private static async Task TeardownAsync(SceneTree sceneTree, Node vadimNode, Node player, Node allyVoice)
    {
        Node root = sceneTree.CurrentScene ?? sceneTree.Root;
        foreach (Node node in new[] { vadimNode, player, allyVoice })
        {
            if (node.IsInsideTree())
            {
                root.RemoveChild(node);
            }
        }

        await TestUtils.WaitForFramesAsync(sceneTree, 4);
    }

    private static void DetachAndFree(SceneTree sceneTree, Node vadimNode, Node player, Node allyVoice)
    {
        Node root = sceneTree.CurrentScene ?? sceneTree.Root;
        foreach (Node node in new[] { vadimNode, player, allyVoice })
        {
            if (GodotObject.IsInstanceValid(node))
            {
                if (node.IsInsideTree())
                {
                    root.RemoveChild(node);
                }

                node.QueueFree();
            }
        }
    }

    private static async Task WaitUntilAsync(
        SceneTree sceneTree,
        Func<bool> predicate,
        string description,
        DateTime deadline,
        CancellationToken cancellationToken)
    {
        while (!predicate())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"The interrogation scenario timed out waiting for {description}.");
            }

            await TestUtils.WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Waits until the session clock reaches the target game time. Coarse quarter-second chunks keep the frame
    /// loop idle during the long approach; the final frames poll per frame so the delivery lands on the first
    /// frame at or after the target game time.
    /// </summary>
    private static async Task WaitForGameTimeAsync(
        SceneTree sceneTree,
        GameClock clock,
        double targetSeconds,
        string description,
        DateTime deadline,
        CancellationToken cancellationToken)
    {
        while (clock.NowSeconds < targetSeconds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"The interrogation scenario timed out waiting for {description}.");
            }

            await (targetSeconds - clock.NowSeconds > 0.75d
                ? Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken)
                : TestUtils.WaitForNextFrameAsync(sceneTree));
        }
    }

    /// <summary>
    /// Deterministic publishing voice: the base <see cref="Voice" /> submission path completes exactly at the
    /// playback hand-off and publishes through the real group publication loop, substituting only audio
    /// generation and playback.
    /// </summary>
    private sealed partial class DeterministicVoice : Voice;

    /// <summary>
    /// Fixture player character for the interrogation scene: a player-group node carrying the publishing Ally
    /// voice component, seated in front of Vadim so the authored addressee inference resolves him as her
    /// addressee.
    /// </summary>
    private sealed partial class InterrogationPlayerCharacter : Node3D, ICharacter
    {
        private readonly IVoice _voice;

        public InterrogationPlayerCharacter(IVoice voice)
        {
            _voice = voice;
            AddToGroup(ISceneContext.PlayerGroupName);
        }
        public string Id { get; set; } = "fixture_player";

        public string Type => "char";

        public string FullId => $"char:{Id}";

        public IReadOnlyList<IComponent> Components => [_voice];

        public IReadOnlyList<VisualCue> VisualCues => [];
    }

    /// <summary>
    /// Session client provider that forwards every provider roundtrip through the executor's traced target client
    /// (AI-002 TR-2): live runs borrow the live target client, and the deterministic smoke borrows the scripted
    /// fake — identical plumbing, no network in the smoke.
    /// </summary>
    internal sealed partial class InterrogationSessionClientProvider : ClientProvider
    {
        private readonly TaskCompletionSource _wiringComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private InterrogationTracingClient? _client;

        public Func<int> SettledTurnCount { get; init; } = static () => 0;

        public LiveLLMScenarioContext? Context
        {
            get;
            set;
        }

        /// <summary>
        /// Releases provider requests after the authored scene's wiring is final, so no request — and therefore no
        /// speech execution — races the deferred rig installation or the deterministic voice swap.
        /// </summary>
        public void SignalWiringComplete() => _ = _wiringComplete.TrySetResult();

        /// <summary>
        /// Snapshot of the completed requests in arrival order; safe to poll from the main thread while provider
        /// continuations append.
        /// </summary>
        public IReadOnlyList<VadimInterrogationRequest> Snapshot()
            => _client?.Snapshot() ?? [];

        /// <summary>Number of requests that have started, including any still awaiting their response.</summary>
        public int StartedRequestCount => _client?.StartedCount ?? 0;

        public override IChatClient CreateChatClient()
        {
            if (_client is null)
            {
                LiveLLMScenarioContext context = Context
                    ?? throw new InvalidOperationException(
                        "The interrogation session client provider needs its scenario context before the session starts.");
                _client = new InterrogationTracingClient(context, SettledTurnCount, _wiringComplete.Task);
            }

            return _client;
        }

        private sealed class InterrogationTracingClient(
            LiveLLMScenarioContext context,
            Func<int> settledTurnCount,
            Task wiringComplete) : IChatClient
        {
            private readonly List<VadimInterrogationRequest> _requests = [];
            private readonly Lock _gate = new();
            private int _startedCount;

            public IReadOnlyList<VadimInterrogationRequest> Snapshot()
            {
                lock (_gate)
                {
                    return [.. _requests];
                }
            }

            public int StartedCount
            {
                get
                {
                    lock (_gate)
                    {
                        return _startedCount;
                    }
                }
            }

            public async Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                IReadOnlyList<ChatMessage> request = [.. messages];
                if (!wiringComplete.IsCompleted)
                {
                    await wiringComplete.WaitAsync(cancellationToken);
                }

                int settledTurnsAtRequestStart = settledTurnCount();
                lock (_gate)
                {
                    _startedCount++;
                }

                ChatResponse response = await context.SendTargetAsync(request, options, cancellationToken);
                lock (_gate)
                {
                    _requests.Add(new VadimInterrogationRequest(
                        _requests.Count, request, response, settledTurnsAtRequestStart));
                }

                return response;
            }

            public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
                => throw new NotSupportedException("The interrogation scenario never uses streaming requests.");

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose()
            {
            }
        }
    }
}
