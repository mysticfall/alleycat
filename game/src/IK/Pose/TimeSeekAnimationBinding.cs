using Godot;

namespace AlleyCat.IK.Pose;

/// <summary>
/// Animation binding that drives a linear crouch-seek clip through an <c>AnimationNodeTimeSeek</c>
/// and (optionally) travels the enclosing <c>AnimationNodeStateMachine</c> to a configured
/// target state while the owning <see cref="PoseState"/> is active.
/// </summary>
/// <remarks>
/// <para>
/// This binding implements the IK-004 default long-continuous transition strategy: the crouch
/// animation clip is scrubbed by a <see cref="AnimationNodeTimeSeek"/> so vertical head motion
/// maps directly to frame position inside the clip without any non-linear blending that would
/// desynchronise the avatar from headset motion (see
/// <c>specs/characters/ik/004-vrik-pose-state-machine/pose-state-machine-contract.md</c>,
/// Technical Requirement #7).
/// </para>
/// <para>
/// All <see cref="AnimationTree"/> parameter paths and state-machine state identifiers are
/// authored as exported fields on this Resource. No parameter name is hard-coded in the
/// binding's logic, so AnimationTree sub-graph reorganisation can be accommodated by updating
/// the <c>.tres</c> without recompiling.
/// </para>
/// <para>
/// The binding is defensive: missing AnimationTree references, empty parameter paths, or an
/// absent playback object are treated as no-ops with a one-time warning so scene authoring
/// errors are surfaced without crashing the runtime loop.
/// </para>
/// <para>
/// Each <see cref="PoseState"/> owns its own binding instance. Because
/// <see cref="PoseStateMachine"/> only applies the <em>active</em> state's binding per tick,
/// the binding's <see cref="TargetStateName"/> can be trusted to represent the desired
/// AnimationTree state without the binding carrying a back-reference to the state machine.
/// </para>
/// </remarks>
[GlobalClass]
public partial class TimeSeekAnimationBinding : AnimationBinding
{
    private bool _warnedMissingTree;
    private bool _warnedMissingSeekPath;
    private bool _warnedMissingPlayback;

    /// <summary>
    /// Full parameter path of the <see cref="AnimationNodeTimeSeek"/> <c>seek_request</c>
    /// property inside the AnimationTree (for example
    /// <c>parameters/Crouch-seek/TimeSeek/seek_request</c>).
    /// </summary>
    /// <remarks>
    /// When empty the seek write is skipped and the binding emits a single warning.
    /// </remarks>
    [Export]
    public StringName SeekRequestParameter
    {
        get;
        set;
    } = new();

    /// <summary>
    /// Parameter path for the root state-machine playback object (for example
    /// <c>parameters/playback</c>). When non-empty, the binding resolves an
    /// <see cref="AnimationNodeStateMachinePlayback"/> and travels to
    /// <see cref="TargetStateName"/> whenever the target changes.
    /// </summary>
    /// <remarks>
    /// Leave empty to disable travel requests (for example when a state is intentionally not
    /// represented as an AnimationTree state-machine node).
    /// </remarks>
    [Export]
    public StringName PlaybackParameter
    {
        get;
        set;
    } = new("parameters/playback");

    /// <summary>
    /// Name of the AnimationTree state-machine state that must be active while the owning
    /// <see cref="PoseState"/> is the active pose. The binding travels to this state whenever
    /// its <see cref="Apply"/> is invoked with a new target.
    /// </summary>
    /// <remarks>
    /// Empty by default; set explicitly per state resource. Standing and Crouching currently
    /// both travel to the same <c>StandingCrouching</c> AnimationTree state, which hosts a
    /// <c>TimeSeek</c>-driven blend tree. The pose-state framework still discriminates the two
    /// states for future divergence (for example when Kneeling introduces a separate
    /// AnimationTree state).
    /// </remarks>
    [Export]
    public StringName TargetStateName
    {
        get;
        set;
    } = new();

    /// <summary>
    /// Duration in seconds of the crouch-seek clip. The normalised 0..1 pose blend is scaled by
    /// this duration to compute the seek time written to <see cref="SeekRequestParameter"/>.
    /// </summary>
    [Export]
    public float ClipDurationSeconds
    {
        get;
        set;
    } = 1.0f;

    /// <summary>
    /// Metres of head vertical descent that map to a fully crouched (1.0) pose blend.
    /// </summary>
    /// <remarks>
    /// Computed as <c>pose_blend = clamp((rest.Y - current.Y) / MaximumCrouchDepthMetres, 0, 1)</c>.
    /// Zero or negative values are clamped to a small epsilon to avoid divide-by-zero.
    /// </remarks>
    [Export]
    public float MaximumCrouchDepthMetres
    {
        get;
        set;
    } = 0.6f;

    /// <inheritdoc />
    public override void Apply(AnimationTree tree, PoseStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (tree is null)
        {
            if (!_warnedMissingTree)
            {
                GD.PushWarning(
                    $"{nameof(TimeSeekAnimationBinding)}.{nameof(Apply)} called with null AnimationTree; skipping.");
                _warnedMissingTree = true;
            }

            return;
        }

        float poseBlend = ComputePoseBlend(
            context.ViewpointGlobalRest.Origin.Y,
            context.CameraTransform.Origin.Y,
            MaximumCrouchDepthMetres);

        WriteSeekRequest(tree, poseBlend);
        MaybeTravel(tree);
    }

    /// <summary>
    /// Computes the normalised 0..1 pose blend from the rest and current head Y values.
    /// </summary>
    /// <remarks>
    /// Exposed as a <see langword="public"/> static helper so the binding's pure math can be
    /// covered by unit tests and reused by sibling classifiers/transitions without instantiating
    /// an AnimationTree. The helper does not mutate engine state and is safe to call on any
    /// thread.
    /// </remarks>
    /// <param name="restHeadY">Calibrated rest viewpoint Y in world space.</param>
    /// <param name="currentHeadY">Current viewpoint Y in world space.</param>
    /// <param name="maximumCrouchDepthMetres">Descent metres that map to a fully crouched blend.</param>
    /// <returns>Clamped pose blend in <c>[0, 1]</c>.</returns>
    public static float ComputePoseBlend(
        float restHeadY,
        float currentHeadY,
        float maximumCrouchDepthMetres)
    {
        // Defensive lower bound so divide-by-zero or negative configuration does not produce
        // NaN or invert the sign of the blend.
        const float DepthFloor = 1e-3f;

        float safeDepth = maximumCrouchDepthMetres > DepthFloor
            ? maximumCrouchDepthMetres
            : DepthFloor;

        float descent = restHeadY - currentHeadY;
        float ratio = descent / safeDepth;
        return Mathf.Clamp(ratio, 0f, 1f);
    }

    private void WriteSeekRequest(AnimationTree tree, float poseBlend)
    {
        if (SeekRequestParameter.IsEmpty)
        {
            if (!_warnedMissingSeekPath)
            {
                GD.PushWarning(
                    $"{nameof(TimeSeekAnimationBinding)}.{nameof(SeekRequestParameter)} is empty; skipping seek writes.");
                _warnedMissingSeekPath = true;
            }

            return;
        }

        float seekTime = poseBlend * ClipDurationSeconds;
        tree.Set(SeekRequestParameter, seekTime);
    }

    private void MaybeTravel(AnimationTree tree)
    {
        if (PlaybackParameter.IsEmpty || TargetStateName.IsEmpty)
        {
            return;
        }

        AnimationNodeStateMachinePlayback? playback =
            tree.Get(PlaybackParameter).As<AnimationNodeStateMachinePlayback>();
        if (playback is null)
        {
            if (!_warnedMissingPlayback)
            {
                GD.PushWarning(
                    $"{nameof(TimeSeekAnimationBinding)} could not resolve playback object at '{PlaybackParameter}'.");
                _warnedMissingPlayback = true;
            }

            return;
        }

        // Travel unconditionally per tick. Godot's default `allow_transition_to_self = false`
        // makes a travel to the already-active state a no-op, while removing the previous
        // per-instance cache avoids the Standing→Crouching→Standing round-trip bug where
        // each binding's stale `_lastTravelledState` would suppress the re-travel and leave
        // the AnimationTree stuck on the previous state.
        playback.Travel(TargetStateName);
    }

    /// <summary>
    /// Pure travel-decision helper exposed for unit coverage of the unconditional travel
    /// semantics introduced in Increment 2 after the Standing → Crouching → Standing
    /// round-trip regression.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Prior revisions cached the last-travelled state per-binding-instance and skipped the
    /// <see cref="AnimationNodeStateMachinePlayback.Travel"/> call when the current target
    /// matched the cache. Because Standing and Crouching own distinct binding instances, the
    /// Standing binding's cache was never invalidated while Crouching was active, and the
    /// subsequent return-to-Standing tick suppressed a legitimate re-travel — leaving the
    /// AnimationTree stuck on <c>Crouch-seek</c>.
    /// </para>
    /// <para>
    /// The fix removes the cache and calls <c>Travel</c> each tick the binding is active.
    /// Travelling to an already-active state is safe: under Godot's default
    /// <c>allow_transition_to_self = false</c> the call is at worst a no-op. This helper
    /// captures that unconditional semantic in a form that can be unit-tested without a
    /// live AnimationTree, by returning <see langword="true"/> whenever the target state
    /// name is non-null/non-empty regardless of the previously travelled state.
    /// </para>
    /// </remarks>
    /// <param name="currentTargetName">
    /// The AnimationTree state-machine state the binding wants to travel to this tick.
    /// <see langword="null"/> or empty values indicate authoring has not mapped the owning
    /// pose state to an AnimationTree state and must suppress the travel call.
    /// </param>
    /// <param name="lastTravelled">
    /// The target previously travelled to (unused by the new unconditional semantic but
    /// retained in the signature so tests can explicitly demonstrate that round-trip state
    /// no longer influences the decision).
    /// </param>
    /// <returns>
    /// <see langword="true"/> if the binding should request a travel this tick;
    /// otherwise <see langword="false"/>.
    /// </returns>
    public static bool ShouldTravel(string? currentTargetName, string? lastTravelled)
    {
        // `lastTravelled` is intentionally ignored. The argument is retained to document the
        // deliberate removal of the previous caching behaviour and to allow tests to assert
        // that round-trip state no longer gates the travel call.
        _ = lastTravelled;

        return !string.IsNullOrEmpty(currentTargetName);
    }
}
