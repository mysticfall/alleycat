using System.Globalization;
using System.Text;
using AlleyCat.Navigation;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Navigation.NAV001ArrivalOutcomeIntegrationTests;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Navigation;

/// <summary>
/// NAV-001 pivot-continuation regression. A stationary 180° turn (the spec's Turn-Only 180° scenario, above the
/// planner's 3×45° pivot-continuation gate) adopts a plan whose same-role pivot restart segment must re-enter the
/// pivot through the rig's continuation alias states: Godot 4.7 state machines cannot author from == to transitions,
/// so a rig without the aliases cannot materialise the restart and the animation driver refuses it every tick
/// (measured baseline: male NPC frozen mid-turn at −113.3° from tick 274, 1,034 driver exceptions, no outcome).
/// Both rigs must complete the request through the real planner and installer, within the configured arrival
/// tolerances, with no driver refusal, and stay exactly still for one second after completion. A rig whose tree
/// lacks the continuation states must fail navigation binding strictly instead of degrading.
/// </summary>
public sealed class NAV001PivotContinuationIntegrationTests
{
    private const string EvidenceDirectory = "res://temp/pivot-alignment-resolution/regression";
    private const int RequestTimeoutTicks = 1200;
    private const int PostCompletionTicks = 60;
    private const string MaleTreePath = "res://assets/characters/templates/animation/animation_tree_root_reference_male_npc.tres";

    /// <summary>The male rig: previously froze mid-turn on the same-role restart; must now complete through its
    /// continuation aliases.</summary>
    [Fact]
    public async Task MaleTurnOnly180_CompletesThroughContinuationAliasesWithoutDriverRefusal()
        => await RunTurnAsync(NPCNavigationRig.Male);

    /// <summary>The female control on the same request: already wired with the continuation aliases.</summary>
    [Fact]
    public async Task FemaleTurnOnly180_CompletesThroughContinuationAliasesWithoutDriverRefusal()
        => await RunTurnAsync(NPCNavigationRig.Female);

    /// <summary>A rig whose tree lacks the continuation states must fail navigation binding strictly: the aliases
    /// are required, not a degrading capability.</summary>
    [Fact]
    public async Task MissingContinuationStates_FailBindingStrictly()
    {
        SceneTree sceneTree = GetSceneTree();
        Node3D root = Assert.IsType<Node3D>(LoadPackedScene(NavigationTestScenePath).Instantiate(), exactMatch: false);
        var controller = (DirectTransformNavigationPlaytestController)root;
        controller.CharacterVariant = NavigationPlaytestCharacterVariant.ReferenceMale;
        controller.ApplyCharacterVariantSelection();
        Node3D actor = root.GetNode<Node3D>(ActorNodeName);
        PrepareActorBeforeTreeEntry(actor);

        // Strip the continuation states from a private in-memory copy of the authored male tree; the tracked
        // resource itself is never modified.
        AnimationNodeBlendTree stripped = ResourceLoader.Load<AnimationNodeBlendTree>(MaleTreePath, cacheMode: ResourceLoader.CacheMode.Ignore)
            ?? throw new InvalidOperationException($"SETUP: '{MaleTreePath}' did not load.");
        AnimationNodeStateMachine machine = Assert.IsType<AnimationNodeStateMachine>(stripped.GetNode("States"));
        for (int index = machine.GetTransitionCount() - 1; index >= 0; index--)
        {
            string from = machine.GetTransitionFrom(index).ToString();
            string to = machine.GetTransitionTo(index).ToString();
            if (from.Contains("Continuation", StringComparison.Ordinal) || to.Contains("Continuation", StringComparison.Ordinal))
            {
                machine.RemoveTransitionByIndex(index);
            }
        }

        machine.RemoveNode("NPCPivotLContinuation");
        machine.RemoveNode("NPCPivotRContinuation");
        Assert.False(machine.HasNode("NPCPivotLContinuation"), "SETUP: stripping left the continuation state behind.");
        actor.GetNode<AnimationTree>("AnimationTree").TreeRoot = stripped;

        sceneTree.Root.AddChild(root);
        try
        {
            NPCNavigation navigation = actor.GetNode<NPCNavigation>("Navigation");
            for (int tick = 0; tick < 120; tick++)
            {
                await WaitForNextPhysicsFrameAsync(sceneTree);
                Assert.False(navigation.IsRuntimeBindingActive,
                    $"A navigation rig without the pivot-continuation states must never activate (tick {tick}).");
            }
        }
        finally
        {
            root.QueueFree();
            await WaitForFramesAsync(sceneTree, 1);
        }
    }

    private static async Task RunTurnAsync(NPCNavigationRig rig)
    {
        await using NPCNavigationRigFixture fixture = await NPCNavigationRigFixture.CreateAsync(rig);
        SceneTree sceneTree = GetSceneTree();
        await fixture.StepAsync(WarmUpPhysicsTicks);
        NPCNavigation navigation = fixture.Navigation;
        Assert.True(navigation.IsRuntimeBindingActive, $"SETUP: {rig} runtime binding inactive at issue time.");

        var refusals = new List<(ulong Tick, string Message)>();
        void OnFirstChance(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs args)
        {
            if (args.Exception is ArgumentException argumentException
                && argumentException.Message.Contains("transition leads from", StringComparison.Ordinal))
            {
                lock (refusals)
                {
                    refusals.Add((Engine.GetPhysicsFrames(), argumentException.Message));
                }
            }
        }

        AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
        var detail = new StringBuilder("tick,current,fading,issued,next_segment,actor_x,actor_z,actor_yaw_deg\n");
        var milestones = new StringBuilder();
        NavigationRequestIdentity identity = IssueRequest(navigation, new ArrivalRequest(Vector3.Zero, Mathf.Pi), $"pivot-continuation-{rig}", 0);
        NavigationTerminalOutcomeRecord? outcome = null;
        string current;
        string fading;
        AnimationNodeStateMachinePlayback playback = fixture.Tree.Get("parameters/States/playback")
            .As<AnimationNodeStateMachinePlayback>();
        for (int tick = 0; tick < RequestTimeoutTicks && outcome is null; tick++)
        {
            await WaitForNextPhysicsFrameAsync(sceneTree);
            using (StringName currentNode = playback.GetCurrentNode())
            {
                current = currentNode.ToString();
            }

            using (StringName fadingNode = playback.GetFadingFromNode())
            {
                fading = fadingNode.ToString();
            }

            Transform3D transform = fixture.Actor.GlobalTransform;
            _ = detail.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{Engine.GetPhysicsFrames()},{current},{fading},{navigation.IssuedClipTransitions.Count},{transform.Origin.X:0.#####},{transform.Origin.Z:0.#####},{Mathf.RadToDeg(transform.Basis.GetEuler().Y):0.###}"));
            outcome = FindOutcome(navigation, identity);
        }

        Assert.True(outcome is not null, $"{rig}: no terminal outcome within {RequestTimeoutTicks} physics ticks.");
        NavigationTerminalOutcomeRecord terminal = outcome!.Value;
        _ = milestones.AppendLine($"outcome={terminal.Outcome} reason={terminal.Reason} terminal_tick={terminal.TerminalPhysicsTick}");

        // A3-style exact stillness: one second after completion, every component of the actor transform is
        // unchanged from the completion transform.
        Transform3D completion = fixture.Actor.GlobalTransform;
        int changed = 0;
        ulong firstChanged = 0;
        for (int tick = 0; tick < PostCompletionTicks; tick++)
        {
            await WaitForNextPhysicsFrameAsync(sceneTree);
            if (fixture.Actor.GlobalTransform != completion)
            {
                changed++;
                firstChanged = changed == 1 ? Engine.GetPhysicsFrames() : firstChanged;
            }
        }

        AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance;

        string transitions = string.Join('\n', navigation.IssuedClipTransitions.Select(record
            => $"{record.PhysicsTick}: {record.From} -> {record.To} entry={record.EntryPositionSeconds:0.######} xfade={record.CrossfadeSeconds}"));
        bool usedAliasReentry = navigation.IssuedClipTransitions.Any(record
            => record.To.ToString().Contains("Continuation", StringComparison.Ordinal));
        string summary = string.Create(
            CultureInfo.InvariantCulture,
            $"{rig} outcome={terminal.Outcome} planar_error_mm={terminal.Error.PlanarDistance * 1000:0.###} yaw_error_deg={Mathf.RadToDeg(terminal.Error.YawRadians):0.####} stillness_changed_ticks={changed}/{PostCompletionTicks} driver_refusals={refusals.Count} alias_reentry_issued={usedAliasReentry} issued_transitions={navigation.IssuedClipTransitions.Count}");

        string directory = EvidenceDestinations.Resolve(ProjectSettings.GlobalizePath($"{EvidenceDirectory}/{rig}"));
        File.WriteAllText(Path.Combine(directory, "summary.txt"), summary);
        File.WriteAllText(Path.Combine(directory, "ticks.csv"), detail.ToString());
        File.WriteAllText(Path.Combine(directory, "milestones.txt"), milestones.ToString());
        File.WriteAllText(Path.Combine(directory, "transitions.txt"), transitions);
        File.WriteAllText(Path.Combine(directory, "driver-refusals.txt"), string.Join('\n', refusals.Select(entry => $"{entry.Tick}: {entry.Message}")));

        Assert.True(terminal.Outcome == NavigationTerminalOutcome.Completed, $"{summary}; transitions:\n{transitions}");
        Assert.True(terminal.Error.PlanarDistance <= navigation.PositionTolerance, $"{summary} (planar tolerance {navigation.PositionTolerance} m).");
        Assert.True(Mathf.Abs(terminal.Error.YawRadians) <= Mathf.DegToRad(navigation.YawToleranceDegrees), $"{summary} (yaw tolerance {navigation.YawToleranceDegrees}°).");
        Assert.True(changed == 0, $"{summary}; stillness first changed at tick {firstChanged}.");
        Assert.True(refusals.Count == 0, $"{summary}; first refusal at tick {(refusals.Count > 0 ? refusals[0].Tick : 0)}: {(refusals.Count > 0 ? refusals[0].Message : "<none>")}");
        Assert.True(usedAliasReentry, $"{summary}; the same-role pivot restart never re-entered through a continuation alias:\n{transitions}");
    }
}
