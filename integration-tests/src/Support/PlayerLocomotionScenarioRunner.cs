using System.Globalization;
using AlleyCat.Control.Locomotion;
using Godot;
using static AlleyCat.IntegrationTests.Support.TestUtils;

#pragma warning disable CS1591

namespace AlleyCat.IntegrationTests.Support;

/// <summary>The kind of scripted release the runner applies during a bout.</summary>
public enum PlayerLocomotionReleaseKind
{
    /// <summary>Control bout: the stick is held for the whole bout and never released.</summary>
    None,

    /// <summary>Release a fraction into the observed i2* entry clip.</summary>
    AtEntryPhase,

    /// <summary>Release a fraction into the observed sustained-travel cycle (the travel-exit path).</summary>
    AtTravelPhase,

    /// <summary>Release one evaluation tick after an observed live travel-clock wrap.</summary>
    AfterEvaluatedTravelWrap,
}

/// <summary>
/// Scripted release schedule for one bout. Phase anchors are observed from the animation graph, not assumed from
/// the push frame: the entry anchor is the first sample whose graph node is an i2* entry, and the travel anchor
/// is the first sample whose graph node is PlayerTravel.
/// </summary>
public sealed record PlayerLocomotionReleaseSchedule(
    PlayerLocomotionReleaseKind Kind,
    float Phase,
    int AfterCycles)
{
    public static PlayerLocomotionReleaseSchedule None { get; } = new(PlayerLocomotionReleaseKind.None, 0f, 0);

    /// <summary>Releases <paramref name="phase"/> (0-1) into the observed declared entry clip.</summary>
    public static PlayerLocomotionReleaseSchedule AtEntryPhase(float phase)
        => new(PlayerLocomotionReleaseKind.AtEntryPhase, phase, 0);

    /// <summary>
    /// Releases <paramref name="phase"/> (0-1) into the observed declared travel cycle, after
    /// <paramref name="afterCycles"/> whole cycles have elapsed since PlayerTravel was first observed. The phase
    /// is a tick-count approximation measured from the observed travel start; the release lands on the first tick
    /// at or after the target time (so the applied input change trails the observation by up to one tick).
    /// </summary>
    public static PlayerLocomotionReleaseSchedule AtTravelPhase(float phase, int afterCycles = 0)
        => new(PlayerLocomotionReleaseKind.AtTravelPhase, phase, afterCycles);

    /// <summary>Releases after the wrapped sample has already been evaluated once with held intent.</summary>
    public static PlayerLocomotionReleaseSchedule OneTickAfterEvaluatedTravelWrap()
        => new(PlayerLocomotionReleaseKind.AfterEvaluatedTravelWrap, 0f, 0);
}

/// <summary>Scripted "push stick → walk → release" bout for the fluidity harness.</summary>
public sealed record PlayerLocomotionBoutPlan(
    string CaseName,
    Vector2 PushInput,
    PlayerLocomotionReleaseSchedule? Release = null,
    float HoldSeconds = 5f,
    float IdleSettleSeconds = 0.5f,
    float PostReleaseObservationSeconds = 1f,
    float MaximumDurationSeconds = 15f,
    Vector2? SustainedInput = null);

/// <summary>The recorded outcome of one bout: per-tick samples, the graph-node timeline, and the release detail.</summary>
public sealed record PlayerLocomotionBoutRecord(
    string CaseName,
    float TickSeconds,
    Vector2 PushInput,
    string ReleaseDetail,
    float? ReleaseTimeSeconds,
    IReadOnlyList<FluiditySample> Samples,
    IReadOnlyList<FluidityNodeSegment> NodeTimeline);

/// <summary>
/// Records per-physics-tick samples from the declared synthetic player composition. It disables the locomotion
/// component's own physics processing, then steps synchronously with Move → _PhysicsProcess → tree.Advance → sample.
/// </summary>
public sealed class PlayerLocomotionScenarioRunner : IDisposable
{
    public const string IdleStateName = SyntheticPlayerLocomotionRuntime.IdleState;
    public const string TravelStateName = SyntheticPlayerLocomotionRuntime.TravelState;

    /// <summary>
    /// The CTRL-001 fluidity case matrix uses only declared synthetic entry and travel clocks. The gate remains
    /// fail-closed because this runner deliberately supplies no contact evidence.
    /// </summary>
    public static IReadOnlyList<PlayerLocomotionBoutPlan> BuildReleaseTimingMatrix()
    {
        Vector2 left = new(0f, 1f);
        Vector2 right = new(0.2f, 1f);
        return
        [
            new PlayerLocomotionBoutPlan("control-stride-hold", left),
            new PlayerLocomotionBoutPlan("control-entry-to-travel-hold", left, HoldSeconds: 2.5f),
            new PlayerLocomotionBoutPlan("release-entry-p15", left, PlayerLocomotionReleaseSchedule.AtEntryPhase(0.15f)),
            new PlayerLocomotionBoutPlan("release-entry-p40", left, PlayerLocomotionReleaseSchedule.AtEntryPhase(0.40f)),
            new PlayerLocomotionBoutPlan("release-entry-p70", left, PlayerLocomotionReleaseSchedule.AtEntryPhase(0.70f)),
            new PlayerLocomotionBoutPlan("release-entry-p95", left, PlayerLocomotionReleaseSchedule.AtEntryPhase(0.95f)),
            new PlayerLocomotionBoutPlan("release-travel-p00", left, PlayerLocomotionReleaseSchedule.AtTravelPhase(0f)),
            new PlayerLocomotionBoutPlan("release-travel-p25", left, PlayerLocomotionReleaseSchedule.AtTravelPhase(0.25f)),
            new PlayerLocomotionBoutPlan("release-travel-p50", left, PlayerLocomotionReleaseSchedule.AtTravelPhase(0.50f)),
            new PlayerLocomotionBoutPlan("release-travel-p75", left, PlayerLocomotionReleaseSchedule.AtTravelPhase(0.75f)),
            new PlayerLocomotionBoutPlan("release-travel-after2-p00", left, PlayerLocomotionReleaseSchedule.AtTravelPhase(0f, 2)),
            new PlayerLocomotionBoutPlan("release-travel-p60", left, PlayerLocomotionReleaseSchedule.AtTravelPhase(0.60f)),
            new PlayerLocomotionBoutPlan("release-travel-after1-p10", left, PlayerLocomotionReleaseSchedule.AtTravelPhase(0.10f, 1)),
            new PlayerLocomotionBoutPlan("right-release-entry-p40", right, PlayerLocomotionReleaseSchedule.AtEntryPhase(0.40f)),
            new PlayerLocomotionBoutPlan("right-release-travel-p50", right, PlayerLocomotionReleaseSchedule.AtTravelPhase(0.50f, 1)),
        ];
    }

    private readonly SyntheticLocomotionRuntimeFixture _fixture;
    private readonly CharacterLocomotion _locomotion;
    private readonly AnimationTree _tree;
    private readonly AnimationNodeStateMachinePlayback _playback;
    private readonly Skeleton3D _skeleton;
    private readonly Node3D _rootMotionReference;
    private Vector3 _virtualRootPosition;
    private readonly int _leftFootBoneIndex;
    private readonly int _rightFootBoneIndex;
    private bool _hasRunBout;
    private bool _disposed;

    /// <summary>Configures candidate provenance before the first manually stepped synthetic tick.</summary>
    public void ConfigureCataloguePath(string path) => _locomotion.CataloguePath = path;

    public PlayerTravelReleaseOccurrenceV1? LastTravelReleaseOccurrence => _locomotion.LastTravelReleaseOccurrence;

    private PlayerLocomotionScenarioRunner(
        SyntheticLocomotionRuntimeFixture fixture,
        SyntheticPlayerLocomotionRuntime runtime)
    {
        _fixture = fixture;
        _locomotion = runtime.Locomotion;
        _tree = runtime.AnimationTree;
        _rootMotionReference = runtime.RootMotionReference;
        _skeleton = runtime.Skeleton;
        _playback = _tree.Get("parameters/States/playback").As<AnimationNodeStateMachinePlayback>()
            ?? throw new InvalidOperationException("Expected the player animation tree to expose the States playback.");
        _playback.Start(IdleStateName, reset: true);
        _tree.Advance(0d);
        _leftFootBoneIndex = RequireBone("LeftFoot");
        _rightFootBoneIndex = RequireBone("RightFoot");
        // Manual stepping owns the whole pipeline; the engine's physics loop must not interleave its own ticks.
        _locomotion.SetPhysicsProcess(false);
    }

    public static async Task<PlayerLocomotionScenarioRunner> CreateAsync(SceneTree sceneTree, int setupFrames = 3)
    {
        var fixture = SyntheticLocomotionRuntimeFixture.Create("player-scenarios");
        SyntheticPlayerLocomotionRuntime runtime = fixture.CreatePlayerLocomotionRuntime();
        _ = sceneTree.Root.CallDeferred(Node.MethodName.AddChild, fixture.Root);
        await WaitForFramesAsync(sceneTree, setupFrames);
        if (!runtime.Actor.IsInsideTree())
        {
            fixture.Dispose();
            throw new InvalidOperationException("Expected the synthetic player composition to be attached to the scene tree.");
        }
        runtime.AnimationTree.Active = true;
        return new PlayerLocomotionScenarioRunner(fixture, runtime);
    }

    /// <summary>
    /// Runs one scripted bout and returns its recorded samples. A runner owns one fixture and may execute exactly
    /// one bout: callers must dispose it and create a fresh runner for another case. This prevents a later case
    /// from observing a stale player lifecycle or release occurrence from an earlier case.
    /// </summary>
    /// <remarks>
    /// Recording caveat: the manual MoveAndSlide runs against Jolt's separate physics thread, so under heavy
    /// concurrent machine load the recorded body trajectory can degrade measurably (stance slip inflates up to
    /// ~10% in loaded runs; a fully saturated machine can double it). Pausing the tree is NOT a fix — a paused
    /// MoveAndSlide only applies a fraction of the motion, collapsing the recording's world-space semantics.
    /// Run the fluidity suites on an unloaded machine.
    /// </remarks>
    public PlayerLocomotionBoutRecord RunBout(PlayerLocomotionBoutPlan plan)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_hasRunBout)
        {
            throw new InvalidOperationException(
                "A PlayerLocomotionScenarioRunner may execute only one bout. Dispose it and create a fresh runner for the next bout.");
        }

        // Mark before any scene mutation: a failed partial execution is not a reusable fixture.
        _hasRunBout = true;
        PlayerLocomotionReleaseSchedule schedule = plan.Release ?? PlayerLocomotionReleaseSchedule.None;
        bool control = schedule.Kind == PlayerLocomotionReleaseKind.None;
        float tick = 1f / Engine.PhysicsTicksPerSecond;
        float controlEndSeconds = plan.IdleSettleSeconds + plan.HoldSeconds;
        int maximumTicks = Mathf.CeilToInt(plan.MaximumDurationSeconds / tick);

        List<FluiditySample> samples = new(maximumTicks);
        List<FluidityNodeSegment> timeline = [];
        string currentNode = _playback.GetCurrentNode().ToString();
        timeline.Add(new FluidityNodeSegment(currentNode, 0f, float.PositiveInfinity));
        string previousNode = currentNode;

        float time = 0f;
        bool released = false;
        float? releaseTime = null;
        string releaseDetail = control
            ? string.Create(CultureInfo.InvariantCulture, $"control hold: input {Format(plan.PushInput)} held for {plan.HoldSeconds:0.###} s after {plan.IdleSettleSeconds:0.###} s idle settle")
            : string.Create(CultureInfo.InvariantCulture, $"scheduled release: kind {schedule.Kind}, phase {schedule.Phase:0.###}, after {schedule.AfterCycles} cycles");
        float? entryStartTime = null;
        string? entryNodeName = null;
        float? travelStartTime = null;
        float? previousTravelPosition = null;
        bool releaseOnNextTravelTick = false;

        // Idle settle: tick with zero input so the bout always starts pushing from a settled idle pose rather
        // than whatever crossfade state the previous bout's drain left behind.
        int settleTicks = Mathf.RoundToInt(plan.IdleSettleSeconds / tick);
        for (int tickIndex = 0; tickIndex < settleTicks; tickIndex++)
        {
            StepAndRecordZeroInput(tick, samples, timeline, ref currentNode, ref previousNode, ref time);
        }

        for (int tickIndex = 0; tickIndex < maximumTicks; tickIndex++)
        {
            Vector2 heldInput = currentNode == TravelStateName && plan.SustainedInput.HasValue
                ? plan.SustainedInput.Value
                : plan.PushInput;
            _locomotion.Move(released ? Vector2.Zero : heldInput);
            _locomotion._PhysicsProcess(tick);
            _tree.Advance(tick);
            time += tick;

            currentNode = _playback.GetCurrentNode().ToString();
            if (currentNode != previousNode)
            {
                timeline[^1] = timeline[^1] with
                {
                    ExitTimeSeconds = time
                };
                timeline.Add(new FluidityNodeSegment(currentNode, time, float.PositiveInfinity));
                previousNode = currentNode;
            }

            if (entryStartTime is null && IsEntryNode(currentNode))
            {
                entryStartTime = time;
                entryNodeName = currentNode;
            }

            if (travelStartTime is null && currentNode == TravelStateName)
            {
                travelStartTime = time;
            }

            samples.Add(RecordSample(time, currentNode));

            if (!released)
            {
                if (schedule.Kind == PlayerLocomotionReleaseKind.AtEntryPhase && entryStartTime is float entryStart)
                {
                    float entryTarget = schedule.Phase * _fixture.Declaration.NavigationManifest["i2f_l"].DurationSeconds;
                    if (time - entryStart >= entryTarget)
                    {
                        released = true;
                        releaseTime = time;
                        releaseDetail = string.Create(CultureInfo.InvariantCulture, $"entry release: phase {schedule.Phase:0.###} of {entryNodeName} (released {time - entryStart:0.###} s after entry observation, target {entryTarget:0.###} s)");
                    }
                }
                else if (schedule.Kind == PlayerLocomotionReleaseKind.AtTravelPhase && travelStartTime is float travelStart)
                {
                    float travelTarget = (schedule.AfterCycles + schedule.Phase) * _fixture.Declaration.NavigationManifest["wf"].DurationSeconds;
                    if (time - travelStart >= travelTarget)
                    {
                        released = true;
                        releaseTime = time;
                        releaseDetail = string.Create(CultureInfo.InvariantCulture, $"travel release: phase {schedule.Phase:0.###} after {schedule.AfterCycles} cycles (released {time - travelStart:0.###} s after PlayerTravel observation, target {travelTarget:0.###} s)");
                    }
                }
                else if (schedule.Kind == PlayerLocomotionReleaseKind.AfterEvaluatedTravelWrap
                    && currentNode == TravelStateName)
                {
                    float currentTravelPosition = _playback.GetCurrentPlayPosition();
                    if (releaseOnNextTravelTick)
                    {
                        released = true;
                        releaseTime = time;
                        releaseDetail = "released one held-intent evaluation after the live PlayerTravel clock wrapped";
                    }
                    else if (previousTravelPosition is float previous && currentTravelPosition < previous)
                    {
                        releaseOnNextTravelTick = true;
                    }

                    previousTravelPosition = currentTravelPosition;
                }
            }

            if (control)
            {
                if (time >= controlEndSeconds)
                {
                    break;
                }
            }
            else if (released)
            {
                if (releaseTime is float releasedAt && time - releasedAt >= plan.PostReleaseObservationSeconds)
                {
                    break;
                }
            }
        }

        timeline[^1] = timeline[^1] with
        {
            ExitTimeSeconds = time
        };
        return new PlayerLocomotionBoutRecord(
            plan.CaseName,
            tick,
            plan.PushInput,
            releaseDetail,
            releaseTime,
            samples,
            timeline);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _fixture.Dispose();
    }

    /// <summary>One zero-input tick with recording, used for the idle settle phase before the push.</summary>
    private void StepAndRecordZeroInput(
        float tick,
        List<FluiditySample> samples,
        List<FluidityNodeSegment> timeline,
        ref string currentNode,
        ref string previousNode,
        ref float time)
    {
        _locomotion.Move(Vector2.Zero);
        _locomotion._PhysicsProcess(tick);
        _tree.Advance(tick);
        time += tick;

        currentNode = _playback.GetCurrentNode().ToString();
        if (currentNode != previousNode)
        {
            timeline[^1] = timeline[^1] with
            {
                ExitTimeSeconds = time
            };
            timeline.Add(new FluidityNodeSegment(currentNode, time, float.PositiveInfinity));
            previousNode = currentNode;
        }

        samples.Add(RecordSample(time, currentNode));
    }

    /// <summary>
    /// Advances the virtual root-motion body and records one sample. The virtual body integrates the tree's
    /// authored root-motion delta per tick, so positioning measurements are deterministic and immune to the
    /// documented MoveAndSlide/Jolt recording stall (the physics body's actual stepped position is NOT
    /// recorded — the authored plan is what positioning variance measures; the residual budget is bounded
    /// separately by CTRL-001 UR9).
    /// </summary>
    private FluiditySample RecordSample(float time, string currentNode)
    {
        _virtualRootPosition += _rootMotionReference.GlobalBasis * _tree.GetRootMotionPosition();
        return new FluiditySample(
            time,
            currentNode,
            BoneWorldPosition(_leftFootBoneIndex),
            BoneWorldPosition(_rightFootBoneIndex),
            _virtualRootPosition);
    }

    private static bool IsEntryNode(string nodeName)
        => nodeName is "PlayerI2FL" or "PlayerI2FR" or "PlayerI2BL" or "PlayerI2BR";

    private Vector3 BoneWorldPosition(int boneIndex)
        => _skeleton.GlobalTransform * _skeleton.GetBoneGlobalPose(boneIndex).Origin;

    private int RequireBone(string boneName)
    {
        int index = _skeleton.FindBone(boneName);
        return index >= 0
            ? index
            : throw new InvalidOperationException($"Expected the player skeleton to expose bone '{boneName}'.");
    }

    private static string Format(Vector2 input)
        => string.Create(CultureInfo.InvariantCulture, $"({input.X:0.###}, {input.Y:0.###})");
}
