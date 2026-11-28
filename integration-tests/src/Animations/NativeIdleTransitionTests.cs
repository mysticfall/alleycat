using AlleyCat.Animations;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using Frame = AlleyCat.IntegrationTests.Animations.NativeIdleTransitionHarness.Frame;

namespace AlleyCat.IntegrationTests.Animations;

/// <summary>
/// ANIM-003 TR13–TR21 on the installed reference female NPC with real MotionBricks inference: native idle ownership,
/// idle↔idle and idle↔locomotion bridges, interruption and locomotion variant switching.
/// </summary>
/// <remarks>
/// Bounds come from the idle-bridge spike on the same rig (<c>game/temp/idle-spike/analysis4.txt</c>), measured per
/// 1/30 s on the final visible pose: settled native idle moves at most 0.32° per frame, while an unbridged cut moves
/// 44.5°; the unblended seam kinks measured 2.70°/frame² (no ease-out) and 2.99°/frame² (interruption without a velocity
/// blend), against a bridge-interior p95 of about 1.2°/frame².
/// </remarks>
[IsolatedProcess]
public sealed class NativeIdleTransitionTests
{

    /// <summary>Largest body-bone rotation per 1/30 s at a writer seam: the settled idle envelope plus margin.</summary>
    private const double SeamPoseBound = .5;

    /// <summary>Largest body-bone angular-velocity change per 1/30 s around a seam or interruption.</summary>
    internal const double SeamAccelerationBound = 1.5;

    /// <summary>Largest unmapped-bone translation per 1/30 s at a writer seam (spike idle joints move about 0.1 cm).</summary>
    private const double UnmappedSeamPositionBound = .002;

    /// <summary>Clip-keyed leg bones; installed foot IK runs after the idle writer and is synced from its pose.</summary>
    private static readonly string[] _legBones = ["LeftUpperLeg", "LeftLowerLeg", "LeftFoot", "RightUpperLeg", "RightLowerLeg", "RightFoot"];

    /// <summary>
    /// Largest final leg deviation from the clip. Foot IK re-solves the legs to targets synced from the idle pose; a
    /// leg following the competing AnimationTree pose instead deviates by the tree's own offset (about 19°).
    /// </summary>
    private const double LegFollowBound = .5;

    /// <summary>
    /// TR13: while a native idle plays, the retargeter never writes, and neither AnimationTree output nor the SOMA source
    /// skeleton reaches the presented body; the clip sets the torso exactly, and the legs follow it through foot IK.
    /// </summary>
    [Fact]
    public async Task NativeIdle_IsTheOnlyBodyWriter()
    {
        await using NativeIdleTransitionHarness harness = await NativeIdleTransitionHarness.StartAsync();
        MotionBricksNavigationProbe probe = new(harness);
        Animation clip = AnimationCatalogue.RequireFor(harness.Actor).Idle.GetClip("sad");
        Skeleton3D source = harness.Retarget.GetParent<Skeleton3D>();
        AnimationTree tree = harness.Actor.GetNode<AnimationTree>("AnimationTree");
        double worst = 0, legs = 0;
        int samples = 0, retargetActive = 0;
        long passes = harness.Navigation.IdleModifier!.WritingPasses;
        // The mixer output of each tick, before the idle writer replaces it.
        double incoming = 0;
        int incomingPasses = 0;
        void Incoming(NativeIdleModifier modifier)
        {
            incomingPasses++;
            incoming = Math.Max(incoming, probe.ClipDeviation(clip, modifier.Time, NativeIdleTransitionHarness.UnmodifiedBones));
        }

        harness.Navigation.IdleModifier.Processing += Incoming;
        void Check()
        {
            if (!harness.Navigation.NativeIdleOwnsBody)
            {
                return;
            }

            retargetActive += harness.Retarget.Active ? 1 : 0;
            worst = Math.Max(worst, probe.ClipDeviation(clip, harness.Navigation.NativeIdleTime, NativeIdleTransitionHarness.UnmodifiedBones));
            legs = Math.Max(legs, probe.ClipDeviation(clip, harness.Navigation.NativeIdleTime, _legBones));
            samples++;
        }

        harness.Skeleton.SkeletonUpdated += Check;
        try
        {
            for (int tick = 0; tick < 90; tick++)
            {
                // Competing writers: the tree's built-in pose is scrubbed and the hidden source skeleton is bent.
                tree.Set("parameters/States/StandingCrouching/TimeSeek/seek_request", tick % 2 == 0 ? 0.0 : 10.0);
                source.SetBonePoseRotation(1, new Quaternion(Vector3.Right, .6f));
                await harness.PhysicsFramesAsync(1);
            }
        }
        finally
        {
            harness.Skeleton.SkeletonUpdated -= Check;
            harness.Navigation.IdleModifier.Processing -= Incoming;
        }

        Note($"native idle samples {samples}, worst clip deviation {worst:F4}°, legs {legs:F4}°, retarget active {retargetActive}, competing mixer output up to {incoming:F2}° over {incomingPasses} passes");
        Assert.True(incoming > 5, $"The competing mixer output ({incoming:F2}°) does not differ from the clip; the check is vacuous.");
        Assert.True(samples >= 80, $"Only {samples} native idle observations.");
        Assert.Equal(0, retargetActive);
        Assert.True(worst < .05, $"Presented body deviated {worst:F4}° from the clip.");
        Assert.True(legs < LegFollowBound, $"Presented legs deviated {legs:F4}° from the clip.");
        Assert.True(harness.Navigation.IdleModifier.WritingPasses - passes >= samples);
        Assert.Equal("initial-idle", harness.Navigation.Phase);
    }

    /// <summary>
    /// TR15–TR19: an idle change bridges from the realised pose into the new idle's start, with ease-in, ease-out, held
    /// actor, carried hips and blended unmapped bones, then plays the target natively. One transition event is raised.
    /// </summary>
    [Fact]
    public async Task IdleToIdle_BridgesIntoNativeTargetWithoutPop()
    {
        await using NativeIdleTransitionHarness harness = await NativeIdleTransitionHarness.StartAsync();
        await harness.WarmUpAsync();
        await harness.PhysicsFramesAsync(30);
        harness.StartRecording();
        await harness.PhysicsFramesAsync(20);
        Vector3 actor = harness.Actor.GlobalPosition;
        Assert.True(harness.Navigation.SetIdlePreference("slump"));
        await harness.WaitUntilAsync(() => !harness.Navigation.NativeIdleOwnsBody, 120, "bridge start");
        IdleBridgePlan plan = Assert.NotNull(harness.Navigation.ActiveBridgePlan);
        await harness.WaitUntilAsync(() => harness.Navigation.NativeIdleOwnsBody, 240, "native slump");
        await harness.PhysicsFramesAsync(60);
        harness.StopRecording();
        harness.Dump("idle_to_idle");

        Assert.Equal(new IdleBridgePlan(true, true, true, true, AnimationTransitionKind.IdleToIdle), plan);
        AnimationTransition transition = Assert.Single(harness.Transitions);
        Assert.Equal((AnimationCategory.Idle, "slump", AnimationTransitionKind.IdleToIdle),
            (transition.Category, transition.Entry.ID, transition.Kind));
        Assert.Same(harness.Actor, transition.Subject);
        Assert.Equal("slump", harness.Navigation.CurrentIdle?.ID);
        Assert.Equal("settled-idle", harness.Navigation.Phase);
        Assert.True(harness.Actor.GlobalPosition.DistanceTo(actor) < 1e-4f, "A stationary idle bridge moved the actor.");
        AssertSeams(harness.Frames, expected: 2);
        AssertUnmappedBlend(harness.Frames);
        AssertHipsCarried(harness.Frames);
    }

    /// <summary>
    /// TR20: a new idle request during a bridge restarts from the current realised pose with a velocity blend; a request
    /// for the bridge's own target changes nothing; a result superseded by a newer request before it arrives is
    /// discarded. Context-age (staleness) regeneration is covered by the <c>StaleContext_*</c> tests.
    /// </summary>
    [Fact]
    public async Task Interruption_RestartsFromRealisedPoseWithVelocityBlend()
    {
        await using NativeIdleTransitionHarness harness = await NativeIdleTransitionHarness.StartAsync();
        await harness.WarmUpAsync();
        await harness.PhysicsFramesAsync(30);
        int discarded = harness.Navigation.DiscardedResults;
        harness.StartRecording();
        await harness.PhysicsFramesAsync(10);

        // Superseded before installation: only the latest request may be installed.
        Assert.True(harness.Navigation.SetIdlePreference("slump"));
        await harness.PhysicsFramesAsync(1);
        Assert.True(harness.Navigation.SetIdlePreference(null));
        Assert.True(harness.Navigation.SetIdlePreference("slump"));
        await harness.WaitUntilAsync(() => !harness.Navigation.NativeIdleOwnsBody, 120, "first bridge");
        _ = Assert.Single(harness.Transitions);
        await harness.PhysicsFramesAsync(24);
        Assert.False(harness.Navigation.NativeIdleOwnsBody);
        Assert.True(harness.Navigation.SetIdlePreference("slump"));
        await harness.PhysicsFramesAsync(4);
        _ = Assert.Single(harness.Transitions);

        long interruptTick = (long)Engine.GetPhysicsFrames();
        Assert.True(harness.Navigation.SetIdlePreference(null));
        await harness.WaitUntilAsync(() => harness.Transitions.Count == 2, 120, "interrupting bridge");
        IdleBridgePlan plan = Assert.NotNull(harness.Navigation.ActiveBridgePlan);
        Assert.True(harness.Navigation.VelocityBlendActive);
        await harness.WaitUntilAsync(() => harness.Navigation.NativeIdleOwnsBody, 240, "native sad");
        await harness.PhysicsFramesAsync(45);
        harness.StopRecording();
        harness.Dump("interruption");

        Assert.Equal(new IdleBridgePlan(false, true, true, true, AnimationTransitionKind.IdleToIdle), plan);
        Assert.Equal(["slump", "sad"], harness.Transitions.Select(t => t.Entry.ID));
        Assert.Equal("sad", harness.Navigation.CurrentIdle?.ID);
        Assert.True(harness.Navigation.DiscardedResults > discarded, "The superseded request result was not rejected.");
        AssertSeams(harness.Frames, expected: 2);
        int install = harness.Frames.ToList().FindIndex(f => f.Tick > interruptTick && f.Blend);
        Assert.True(install > 4, "No blended interruption install was observed.");
        double acceleration = Enumerable.Range(install, 8).Where(i => i < harness.Frames.Count)
            .Max(i => NativeIdleTransitionHarness.MaxAcceleration(harness.Frames[i - 4], harness.Frames[i - 2], harness.Frames[i]));
        // Same-run reference: the replacement bridge's own interior acceleration, away from the blend and the seams.
        int blendEnd = Enumerable.Range(install, harness.Frames.Count - install).First(i => !harness.Frames[i].Blend);
        int seamIn = Enumerable.Range(blendEnd, harness.Frames.Count - blendEnd).First(i => harness.Frames[i].NativeIdle);
        double interior = Enumerable.Range(blendEnd + 12, Math.Max(0, seamIn - blendEnd - 24))
            .Select(i => NativeIdleTransitionHarness.MaxAcceleration(harness.Frames[i - 4], harness.Frames[i - 2], harness.Frames[i])).DefaultIfEmpty().Max();
        double before = Enumerable.Range(install - 12, 8)
            .Max(i => NativeIdleTransitionHarness.MaxAcceleration(harness.Frames[i - 4], harness.Frames[i - 2], harness.Frames[i]));
        Note($"interruption acceleration {acceleration:F3}°/frame² (outgoing bridge before {before:F3}, replacement interior {interior:F3}); install phase {harness.Frames[install].Time * 30 % 1:F2} frame");
        Assert.True(acceleration <= SeamAccelerationBound, $"Interruption velocity kink {acceleration:F3}°/frame².");
        AssertUnmappedBlend(harness.Frames);
    }

    /// <summary>
    /// TR15, TR21: a move request bridges from the native idle into locomotion, and arrival bridges into the effective
    /// idle's start, then the idle plays natively.
    /// </summary>
    [Fact]
    public async Task IdleToLocomotionAndBack_BridgesThroughNavigation()
    {
        await using NativeIdleTransitionHarness harness = await NativeIdleTransitionHarness.StartAsync();
        await harness.WarmUpAsync();
        await harness.PhysicsFramesAsync(30);
        harness.StartRecording();
        await harness.PhysicsFramesAsync(20);
        Vector3 start = harness.Actor.GlobalPosition;
        var destination = new Transform3D(Basis.Identity, start + new Vector3(0, 0, -1.6f));
        Assert.Equal(0, harness.Scene.Call("_submit_destination", destination, Array.Empty<Vector3>()).AsInt32());
        await harness.WaitUntilAsync(() => !harness.Navigation.NativeIdleOwnsBody, 240, "departure");
        IdleBridgePlan departure = Assert.NotNull(harness.Navigation.ActiveBridgePlan);
        await harness.WaitUntilAsync(() => harness.Navigation.NativeIdleOwnsBody && harness.Navigation.Phase == "settled-idle", 900, "arrival into idle");
        await harness.PhysicsFramesAsync(60);
        harness.StopRecording();
        harness.Dump("idle_locomotion_idle");

        Assert.Equal("", harness.Navigation.Failure);
        Assert.Equal(new IdleBridgePlan(true, false, false, true, AnimationTransitionKind.IdleToLocomotion), departure);
        Assert.Equal([(AnimationTransitionKind.IdleToLocomotion, "feminine"), (AnimationTransitionKind.LocomotionToIdle, "sad")],
            harness.Transitions.Select(t => (t.Kind, t.Entry.ID)));
        Assert.True(harness.Actor.GlobalPosition.DistanceTo(destination.Origin) < .35f,
            $"Arrived {harness.Actor.GlobalPosition.DistanceTo(destination.Origin):F3} m from the destination.");
        AssertSeams(harness.Frames, expected: 2);
        AssertUnmappedBlend(harness.Frames);
    }

    /// <summary>
    /// TR15, ANIM-002 TR4: switching the locomotion variant while moving samples the new set for later windows, rejects
    /// stale work, announces the switch once and completes the move. Unknown ids are refused without change.
    /// </summary>
    [Fact]
    public async Task LocomotionVariantSwitch_SamplesNewVariantWhileMoving()
    {
        await using NativeIdleTransitionHarness harness = await NativeIdleTransitionHarness.StartAsync();
        Assert.False(harness.Navigation.SetIdlePreference("missing"));
        Assert.False(harness.Navigation.SetLocomotionPreference("missing"));
        Assert.Equal("feminine", harness.Navigation.CurrentLocomotion?.ID);
        await harness.WarmUpAsync();
        Vector3 start = harness.Actor.GlobalPosition;
        var destination = new Transform3D(Basis.Identity, start + new Vector3(0, 0, -3f));
        Assert.Equal(0, harness.Scene.Call("_submit_destination", destination, Array.Empty<Vector3>()).AsInt32());
        await harness.WaitUntilAsync(() => harness.Navigation.Phase is "departure" or "sustained-walk", 300, "walking");
        int windows = harness.Windows.Count;
        Assert.True(harness.Navigation.SetLocomotionPreference("stroll"));
        await harness.WaitUntilAsync(() => harness.Transitions.Any(t => t.Kind == AnimationTransitionKind.LocomotionToLocomotion), 300, "variant switch");
        await harness.WaitUntilAsync(() => harness.Navigation.NativeIdleOwnsBody && harness.Navigation.Phase == "settled-idle", 900, "arrival");

        Assert.Equal("", harness.Navigation.Failure);
        Assert.Equal("stroll", harness.Navigation.CurrentLocomotion?.ID);
        Assert.True(harness.Windows.Count > windows);
        Assert.Equal(1, harness.Transitions.Count(t => t.Kind == AnimationTransitionKind.LocomotionToLocomotion));
        Assert.Equal("stroll", harness.Transitions.Single(t => t.Kind == AnimationTransitionKind.LocomotionToLocomotion).Entry.ID);
        Assert.Equal(AnimationTransitionKind.LocomotionToIdle, harness.Transitions[^1].Kind);
    }

    /// <summary>
    /// TR20 (B1): from a native idle, X → Y (pending) → X cancels Y. Y never installs or announces, and X stays.
    /// </summary>
    [Fact]
    public async Task Redirect_FromNativeIdleBackToCurrentCancelsPendingTarget()
    {
        await using NativeIdleTransitionHarness harness = await NativeIdleTransitionHarness.StartAsync();
        await harness.WarmUpAsync();
        await harness.PhysicsFramesAsync(20);
        int discarded = harness.Navigation.DiscardedResults;
        int windows = harness.Windows.Count;
        // Hold the slump generation long enough to redirect while it is pending, but below the staleness limit.
        harness.DelayNextGeneration(TimeSpan.FromMilliseconds(250));
        Assert.True(harness.Navigation.SetIdlePreference("slump"));
        await harness.WaitUntilAsync(() => harness.Windows.Count > windows, 30, "slump submission");
        long slump = harness.Windows[^1];
        Assert.True(harness.Navigation.SetIdlePreference(null));
        await harness.PhysicsFramesAsync(90);

        Assert.DoesNotContain(slump, harness.Installed);
        Assert.Empty(harness.Transitions);
        Assert.True(harness.Navigation.NativeIdleOwnsBody);
        Assert.Equal("sad", harness.Navigation.CurrentIdle?.ID);
        Assert.Equal(windows + 1, harness.Windows.Count);
        Assert.True(harness.Navigation.DiscardedResults > discarded, "The cancelled slump result was not discarded.");
        Assert.Equal("", harness.Navigation.Failure);
    }

    /// <summary>
    /// TR20 (B1): during an X bridge, X → Y (pending) → X cancels Y; the X bridge plays on into native X with no
    /// further event.
    /// </summary>
    [Fact]
    public async Task Redirect_DuringBridgeBackToItsTargetCancelsPendingTarget()
    {
        await using NativeIdleTransitionHarness harness = await NativeIdleTransitionHarness.StartAsync();
        await harness.WarmUpAsync();
        await harness.PhysicsFramesAsync(20);
        Assert.True(harness.Navigation.SetIdlePreference("slump"));
        await harness.WaitUntilAsync(() => !harness.Navigation.NativeIdleOwnsBody, 120, "slump bridge");
        await harness.PhysicsFramesAsync(12);
        int windows = harness.Windows.Count;
        harness.DelayNextGeneration(TimeSpan.FromMilliseconds(250));
        Assert.True(harness.Navigation.SetIdlePreference(null));
        await harness.WaitUntilAsync(() => harness.Windows.Count > windows, 30, "sad submission");
        long sad = harness.Windows[^1];
        Assert.False(harness.Navigation.NativeIdleOwnsBody);
        Assert.True(harness.Navigation.SetIdlePreference("slump"));
        await harness.WaitUntilAsync(() => harness.Navigation.NativeIdleOwnsBody, 240, "native slump");
        await harness.PhysicsFramesAsync(30);

        Assert.DoesNotContain(sad, harness.Installed);
        Assert.Equal(["slump"], harness.Transitions.Select(t => t.Entry.ID));
        Assert.Equal("slump", harness.Navigation.CurrentIdle?.ID);
        Assert.Equal("settled-idle", harness.Navigation.Phase);
        Assert.Equal(windows + 1, harness.Windows.Count);
    }

    /// <summary>TR20 (B2): an idle bridge whose realised context aged past the limit is regenerated, never installed.</summary>
    [Fact]
    public async Task StaleContext_IdleBridgeIsRegeneratedNotInstalled()
    {
        await using NativeIdleTransitionHarness harness = await NativeIdleTransitionHarness.StartAsync();
        await harness.WarmUpAsync();
        await harness.PhysicsFramesAsync(20);
        int stale = harness.Navigation.StaleBridgeRegenerations, windows = harness.Windows.Count;
        // The delay exceeds MaxBridgeContextAge (0.5 s) of physics time while the native idle keeps playing.
        harness.DelayNextGeneration(TimeSpan.FromMilliseconds(900));
        Assert.True(harness.Navigation.SetIdlePreference("slump"));
        await harness.WaitUntilAsync(() => harness.Windows.Count > windows, 30, "slump submission");
        long first = harness.Windows[^1];
        await harness.WaitUntilAsync(() => harness.Navigation.StaleBridgeRegenerations > stale, 240, "stale rejection");
        Assert.True(harness.Navigation.NativeIdleOwnsBody, "The body stopped playing the idle while generation was slow.");
        await harness.WaitUntilAsync(() => harness.Navigation.NativeIdleOwnsBody && harness.Navigation.CurrentIdle?.ID == "slump" &&
            harness.Installed.Count > 0, 360, "regenerated slump");

        Assert.DoesNotContain(first, harness.Installed);
        Assert.Contains(harness.Installed, ticket => ticket > first);
        Assert.Equal(stale + 1, harness.Navigation.StaleBridgeRegenerations);
        Assert.Equal(["slump"], harness.Transitions.Select(t => t.Entry.ID));
    }

    /// <summary>
    /// TR20 (B2): a departure from a native idle whose realised context aged past the limit is regenerated, never
    /// installed; the regenerated departure starts at its elapsed time and the move completes.
    /// </summary>
    [Fact]
    public async Task StaleContext_DepartureIsRegeneratedNotInstalled()
    {
        await using NativeIdleTransitionHarness harness = await NativeIdleTransitionHarness.StartAsync();
        await harness.WarmUpAsync();
        await harness.PhysicsFramesAsync(20);
        int stale = harness.Navigation.StaleBridgeRegenerations, windows = harness.Windows.Count;
        harness.DelayNextGeneration(TimeSpan.FromMilliseconds(900));
        var destination = new Transform3D(Basis.Identity, harness.Actor.GlobalPosition + new Vector3(0, 0, -1.6f));
        Assert.Equal(0, harness.Scene.Call("_submit_destination", destination, Array.Empty<Vector3>()).AsInt32());
        await harness.WaitUntilAsync(() => harness.Windows.Count > windows, 60, "departure submission");
        long first = harness.Windows[^1];
        await harness.WaitUntilAsync(() => harness.Navigation.StaleBridgeRegenerations > stale, 240, "stale rejection");
        Assert.True(harness.Navigation.NativeIdleOwnsBody, "The body stopped playing the idle while generation was slow.");
        await harness.WaitUntilAsync(() => !harness.Navigation.NativeIdleOwnsBody, 240, "regenerated departure");

        Assert.DoesNotContain(first, harness.Installed);
        Assert.Equal(stale + 1, harness.Navigation.StaleBridgeRegenerations);
        Assert.Equal(new IdleBridgePlan(true, false, false, true, AnimationTransitionKind.IdleToLocomotion), harness.Navigation.ActiveBridgePlan);
        await harness.WaitUntilAsync(() => harness.Navigation.NativeIdleOwnsBody && harness.Navigation.Phase == "settled-idle", 900, "arrival");
        Assert.Equal("", harness.Navigation.Failure);
        Assert.Equal([AnimationTransitionKind.IdleToLocomotion, AnimationTransitionKind.LocomotionToIdle], harness.Transitions.Select(t => t.Kind));
    }

    /// <summary>
    /// TR13 (B3): the idle writer is the first skeleton modifier once navigation and CharacterIK are both bound, in
    /// either initialisation order; the IK stage pipeline keeps its order after it.
    /// </summary>
    [Fact]
    public async Task IdleWriter_StaysFirstAcrossCharacterIKBinding()
    {
        await using NativeIdleTransitionHarness harness = await NativeIdleTransitionHarness.StartAsync();
        string[] before = ModifierOrder(harness);
        AssertWriterFirst(harness, before);
        Assert.Contains("CharacterIKBeginStage", before);

        // CharacterIK re-binds after the idle writer exists (the reverse of the startup order).
        AlleyCat.IK.CharacterIK ik = harness.Actor.GetNode<AlleyCat.IK.CharacterIK>("CharacterIK");
        ik.ResetRuntimeBindings();
        await harness.WaitUntilAsync(() => ModifierOrder(harness).Contains("CharacterIKBeginStage"), 120, "CharacterIK rebinding");
        await harness.PhysicsFramesAsync(5);
        string[] after = ModifierOrder(harness);
        Note("modifier order before: " + string.Join(", ", before));
        Note("modifier order after CharacterIK rebind: " + string.Join(", ", after));
        AssertWriterFirst(harness, after);
        Assert.Equal(before, after);
    }

    /// <summary>
    /// TR13, TAC-002 (B3): a bone the idle clip does not key keeps the AnimationTree's conflicting pose in presentation,
    /// while keyed bones keep the clip.
    /// </summary>
    [Fact]
    public async Task NativeIdle_LeavesUnkeyedBonesToTheMixer()
    {
        await using NativeIdleTransitionHarness harness = await NativeIdleTransitionHarness.StartAsync();
        MotionBricksNavigationProbe probe = new(harness);
        IdleAnimationView view = AnimationCatalogue.RequireFor(harness.Actor).Idle;
        Animation clip = view.GetClip("sad");
        // The production writer plays a copy of the clip without its Chest tracks; the tree keys Chest.
        var partial = (Animation)clip.Duplicate();
        for (int track = partial.GetTrackCount() - 1; track >= 0; track--)
        {
            if (partial.TrackGetPath(track).GetSubName(0) == "Chest")
            {
                partial.RemoveTrack(track);
            }
        }

        var unkeyed = new NativeIdleClip(view.Find("sad")!, partial, harness.Skeleton);
        int chest = harness.Bone("Chest");
        Assert.False(unkeyed.Keys(chest));
        harness.Navigation.IdleModifier!.PlayIdle(unkeyed, harness.Navigation.NativeIdleTime);
        AnimationTree tree = harness.Actor.GetNode<AnimationTree>("AnimationTree");
        Quaternion mixer = Quaternion.Identity;
        double drift = 0, keyed = 0, swing = 0;
        Quaternion? previous = null;
        int samples = 0;
        void Incoming(NativeIdleModifier modifier)
        {
            mixer = harness.Skeleton.GetBonePoseRotation(chest);
        }
        void Presented()
        {
            Quaternion shown = harness.Skeleton.GetBonePoseRotation(chest);
            drift = Math.Max(drift, Mathf.RadToDeg(mixer.AngleTo(shown)));
            swing = previous is { } last ? Math.Max(swing, Mathf.RadToDeg(last.AngleTo(mixer))) : swing;
            previous = mixer;
            keyed = Math.Max(keyed, probe.ClipDeviation(clip, harness.Navigation.NativeIdleTime, ["Hips", "Spine", "UpperChest"]));
            samples++;
        }

        harness.Navigation.IdleModifier.Processing += Incoming;
        harness.Skeleton.SkeletonUpdated += Presented;
        try
        {
            for (int tick = 0; tick < 40; tick++)
            {
                tree.Set("parameters/States/StandingCrouching/TimeSeek/seek_request", tick % 2 == 0 ? 0.0 : 10.0);
                await harness.PhysicsFramesAsync(1);
            }
        }
        finally
        {
            harness.Navigation.IdleModifier.Processing -= Incoming;
            harness.Skeleton.SkeletonUpdated -= Presented;
        }

        Note($"unkeyed Chest: presented vs mixer {drift:F4}°, mixer swing {swing:F2}°, keyed deviation {keyed:F4}° over {samples} ticks");
        Assert.True(samples >= 35);
        Assert.True(swing > 1, $"The tree's Chest pose did not change ({swing:F2}°); the check is vacuous.");
        // 0.05° is the identity floor: float32 AngleTo reports 2·acos(0.99999994) ≈ 0.0396° for equal rotations.
        Assert.True(drift < .05, $"The unkeyed Chest deviated {drift:F4}° from the mixer pose.");
        Assert.True(keyed < .05, $"Keyed bones deviated {keyed:F4}° from the clip.");
    }

    /// <summary>
    /// TR14, TR16: an idle target window is at least four consecutive 30 Hz frames from the clip start, and its first
    /// frame matches the realised reading of the native clip at its start.
    /// </summary>
    [Fact]
    public async Task IdleTargetWindow_IsTheClipStart()
    {
        await using NativeIdleTransitionHarness harness = await NativeIdleTransitionHarness.StartAsync();
        await harness.WarmUpAsync();
        Animation clip = AnimationCatalogue.RequireFor(harness.Actor).Idle.GetClip("slump");
        int windows = harness.Windows.Count;
        Assert.True(harness.Navigation.SetIdlePreference("slump"));
        await harness.WaitUntilAsync(() => harness.Windows.Count > windows, 30, "slump submission");
        Transform3D[][] target = harness.Targets[harness.Windows[^1]];
        Transform3D[]? realised = null;
        double realisedAt = double.NaN;
        void Observed()
        {
            if (realised is null && harness.Navigation.NativeIdleOwnsBody && harness.Navigation.CurrentIdle?.ID == "slump")
            {
                realised = harness.Navigation.RealisedFeedback.ToArray();
                realisedAt = harness.Navigation.NativeIdleTime;
            }
        }

        harness.Navigation.PoseObserved += Observed;
        try
        {
            await harness.WaitUntilAsync(() => realised is not null, 360, "native slump observation");
        }
        finally
        {
            harness.Navigation.PoseObserved -= Observed;
        }

        Assert.True(target.Length >= 4, $"Target window has {target.Length} frames.");
        // Frame-to-frame rotation of each converted channel equals the clip's own motion between 30 Hz samples.
        string[] bones = ["Spine", "Chest", "Neck", "Head", "LeftUpperArm", "RightUpperArm", "LeftUpperLeg", "RightUpperLeg"];
        SomaChannelProbe channels = new(harness);
        double motion = 0, worst = 0;
        foreach (string bone in bones)
        {
            int soma = channels.Soma(bone);
            int track = clip.FindTrack(new NodePath("%GeneralSkeleton:" + bone), Animation.TrackType.Rotation3D);
            for (int f = 1; f < target.Length; f++)
            {
                double expected = PreciseAngle(clip.RotationTrackInterpolate(track, 0), clip.RotationTrackInterpolate(track, f / 30.0));
                double actual = PreciseAngle(target[0][soma].Basis.GetRotationQuaternion(), target[f][soma].Basis.GetRotationQuaternion());
                worst = Math.Max(worst, Math.Abs(expected - actual));
                motion = Math.Max(motion, expected);
            }
        }

        // The first frame equals the realised native pose read at the clip start (independent reader path).
        double start = 0;
        foreach (string bone in bones)
        {
            int soma = channels.Soma(bone);
            start = Math.Max(start, Mathf.RadToDeg(target[0][soma].Basis.GetRotationQuaternion().AngleTo(realised![soma].Basis.GetRotationQuaternion())));
        }

        Note($"target window {target.Length} frames: relative-motion error {worst:F4}° (clip motion up to {motion:F4}°); frame 0 vs realised native start at {realisedAt:F3}s {start:F4}°");
        Assert.True(worst < .01, $"Target window motion differs from the clip start by {worst:F4}°.");
        Assert.True(realisedAt < 2.0 / 30, $"First native observation was at {realisedAt:F3}s, not the clip start.");
        Assert.True(start < .3, $"Target frame 0 differs {start:F4}° from the realised native clip start.");
    }

    /// <summary>Angle between rotations in degrees, via atan2 in double precision so sub-degree angles stay accurate.</summary>
    private static double PreciseAngle(Quaternion a, Quaternion b)
    {
        // Relative rotation r = a⁻¹ b, expanded in double.
        double ax = -a.X, ay = -a.Y, az = -a.Z, aw = a.W, bx = b.X, by = b.Y, bz = b.Z, bw = b.W;
        double x = (aw * bx) + (ax * bw) + (ay * bz) - (az * by);
        double y = (aw * by) - (ax * bz) + (ay * bw) + (az * bx);
        double z = (aw * bz) + (ax * by) - (ay * bx) + (az * bw);
        double w = (aw * bw) - (ax * bx) - (ay * by) - (az * bz);
        return 2 * Math.Atan2(Math.Sqrt((x * x) + (y * y) + (z * z)), Math.Abs(w)) * 180 / Math.PI;
    }

    private static string[] ModifierOrder(NativeIdleTransitionHarness harness)
        => [.. harness.Skeleton.GetChildren().OfType<SkeletonModifier3D>().Select(m => m.Name.ToString())];

    private static void AssertWriterFirst(NativeIdleTransitionHarness harness, string[] order)
    {
        Assert.Equal(harness.Navigation.IdleModifier!.Name.ToString(), order[0]);
        Assert.Equal(1, order.Count(name => name == "NativeIdleModifier"));
    }

    /// <summary>Maps native bone names to canonical SOMA channel indices through the hidden source skeleton.</summary>
    private sealed class SomaChannelProbe(NativeIdleTransitionHarness harness)
    {
        private readonly Skeleton3D _source = harness.Retarget.GetParent<Skeleton3D>();

        public int Soma(string bone)
        {
            int index = _source.FindBone(bone);
            Assert.True(index > 0, $"No SOMA channel named {bone}.");
            return index;
        }
    }

    internal static void AssertSeams(IReadOnlyList<Frame> frames, int expected)
    {
        List<int> seams = [.. Enumerable.Range(1, frames.Count - 1).Where(i => frames[i].NativeIdle != frames[i - 1].NativeIdle)];
        Assert.Equal(expected, seams.Count);
        double idle = Enumerable.Range(2, frames.Count - 2).Where(i => frames[i].NativeIdle && frames[i - 2].NativeIdle)
            .Select(i => NativeIdleTransitionHarness.MaxRotationDelta(frames[i - 2], frames[i]).Degrees).DefaultIfEmpty().Max();
        foreach (int seam in seams)
        {
            Assert.True(seam >= 4 && seam + 3 < frames.Count, $"Seam {seam} lacks a surrounding observation window.");
            double pose = Enumerable.Range(seam, 2).Max(i => NativeIdleTransitionHarness.MaxRotationDelta(frames[i - 2], frames[i]).Degrees);
            double acceleration = Enumerable.Range(seam, 3)
                .Max(i => NativeIdleTransitionHarness.MaxAcceleration(frames[i - 4], frames[i - 2], frames[i]));
            Note($"seam at {frames[seam].Tick} ({(frames[seam].NativeIdle ? "into native idle" : "out of native idle")}): pose {pose:F3}°/frame, acceleration {acceleration:F3}°/frame², settled idle max {idle:F3}°/frame");
            Assert.True(pose <= SeamPoseBound, $"Pose pop of {pose:F3}° per frame at the seam at tick {frames[seam].Tick}.");
            Assert.True(acceleration <= SeamAccelerationBound, $"Velocity kink of {acceleration:F3}°/frame² at the seam at tick {frames[seam].Tick}.");
        }
    }

    // TR19: bones outside the SOMA map blend through the bridge instead of holding and then snapping at a seam. They may
    // follow the body (forearm twists track the arms), but never step at a writer seam or outpace the body.
    internal static void AssertUnmappedBlend(IReadOnlyList<Frame> frames)
    {
        double rotation = 0, position = 0, body = 0, seamRotation = 0, seamPosition = 0;
        for (int i = 2; i < frames.Count; i++)
        {
            (double r, double p) = NativeIdleTransitionHarness.UnmappedDelta(frames[i - 2], frames[i]);
            rotation = Math.Max(rotation, r);
            position = Math.Max(position, p);
            body = Math.Max(body, NativeIdleTransitionHarness.MaxRotationDelta(frames[i - 2], frames[i]).Degrees);
            bool seam = frames[i].NativeIdle != frames[i - 1].NativeIdle || (i + 1 < frames.Count && frames[i + 1].NativeIdle != frames[i].NativeIdle);
            if (seam)
            {
                seamRotation = Math.Max(seamRotation, r);
                seamPosition = Math.Max(seamPosition, p);
            }
        }

        Note($"unmapped bones max change per frame: {rotation:F3}° (body {body:F3}°), {position * 100:F3} cm; at seams {seamRotation:F3}°, {seamPosition * 100:F3} cm");
        Assert.True(seamRotation <= SeamPoseBound, $"Unmapped bone snapped by {seamRotation:F3}° at a seam.");
        Assert.True(seamPosition <= UnmappedSeamPositionBound, $"Unmapped bone snapped by {seamPosition * 100:F3} cm at a seam.");
        Assert.True(rotation <= body, $"Unmapped bone moved {rotation:F3}° per frame, faster than the body ({body:F3}°).");
    }

    private static void AssertHipsCarried(IReadOnlyList<Frame> frames)
    {
        // The hips keep their lateral offset from the actor through the bridge rather than being pinned above it.
        double[] offsets = [.. frames.Where(f => !f.NativeIdle).Select(f => (double)new Vector2(f.HipsWorld.X - f.ActorWorld.X, f.HipsWorld.Z - f.ActorWorld.Z).Length())];
        double hipsStep = Enumerable.Range(2, frames.Count - 2).Max(i => frames[i].HipsWorld.DistanceTo(frames[i - 2].HipsWorld));
        Note($"bridge hips offset {offsets.Min() * 100:F2}–{offsets.Max() * 100:F2} cm, hips max step {hipsStep * 100:F3} cm/frame");
        Assert.True(offsets.Max() > .002, "Hips were pinned above the actor through the bridge.");
        Assert.True(hipsStep < .01, $"Hips jumped {hipsStep * 100:F3} cm in one frame.");
    }

    /// <summary>Reads the presented pose against the clip resource itself, independent of the runtime sampler.</summary>
    private sealed class MotionBricksNavigationProbe(NativeIdleTransitionHarness harness)
    {
        public double ClipDeviation(Animation clip, double time, IEnumerable<string> bones)
        {
            double worst = 0;
            foreach (string bone in bones)
            {
                int track = clip.FindTrack(new NodePath("%GeneralSkeleton:" + bone), Animation.TrackType.Rotation3D);
                Assert.True(track >= 0, $"Clip has no rotation track for {bone}.");
                Quaternion expected = clip.RotationTrackInterpolate(track, time);
                Quaternion actual = harness.Skeleton.GetBonePoseRotation(harness.Bone(bone));
                worst = Math.Max(worst, Mathf.RadToDeg(expected.Normalized().AngleTo(actual)));
            }

            return worst;
        }
    }

    /// <summary>Appends diagnostic measurements to <c>res://temp/anim003-transitions/traces/measurements.log</c>.</summary>
    internal static void Note(string line)
    {
        if (!NativeIdleTransitionHarness.WritesTraces)
        {
            return;
        }

        string directory = ProjectSettings.GlobalizePath("res://temp/anim003-transitions/traces/");
        _ = Directory.CreateDirectory(directory);
        File.AppendAllText(Path.Combine(directory, "measurements.log"), $"{DateTime.Now:O} {line}{System.Environment.NewLine}");
    }
}
