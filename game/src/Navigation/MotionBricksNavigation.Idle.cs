using AlleyCat.Animations;
using AlleyCat.Motion;
using Godot;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Navigation;

/// <summary>
/// ANIM-003 TR12–TR21 for NPCs: catalogue idles play natively on the reference skeleton; MotionBricks bridges connect
/// idles to each other and to locomotion from realised context, with settle easing, hips carry, unmapped-bone blending
/// and interruption velocity blending.
/// </summary>
public partial class MotionBricksNavigation : IAnimationVariantControl
{
    /// <summary>Playback seconds of the settle ease-in at the start of a bridge leaving a native idle (TR18).</summary>
    [ExportGroup("Idle Transitions")]
    [Export(PropertyHint.Range, "0,1,0.01")] public double IdleEaseIn { get; set; } = .2;

    /// <summary>Playback seconds of the settle ease-out into an idle clip's frame 0 (TR18).</summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public double IdleEaseOut { get; set; } = 1.0 / 3;

    /// <summary>Seconds of the velocity blend from a still-moving presentation into a new bridge (TR20).</summary>
    [Export(PropertyHint.Range, "0,0.5,0.01")] public double TransitionBlendTime { get; set; } = 8.0 / 30;

    /// <summary>
    /// Oldest realised context, in seconds, a bridge leaving a still-moving presentation may start from. Older results
    /// (a cold model load) are discarded and regenerated from fresh realised context while the body keeps moving.
    /// </summary>
    [Export(PropertyHint.Range, "0.05,5,0.01")] public double MaxBridgeContextAge { get; set; } = .5;

    private sealed record IdleTarget(NativeIdleClip Clip, Basis Yaw, Vector3 EndOffset, bool Stationary);

    private sealed class BridgeState(Transform3D[][] segment, BridgeTimeWarp warp, IdleBridgePlan plan,
        AnimationCategory category, AnimationEntry entry, IdleTarget? idle, Vector3 startOffset)
    {
        public Transform3D[][] Segment { get; } = segment;
        public BridgeTimeWarp Warp { get; } = warp;
        public IdleBridgePlan Plan { get; } = plan;
        public AnimationCategory Category { get; } = category;
        public AnimationEntry Entry { get; } = entry;
        public IdleTarget? Idle { get; } = idle;
        public Vector3 StartOffset { get; } = startOffset;
        public double Clock
        {
            get; set;
        }
        public bool Finished => Clock >= Warp.PlaybackDuration;
    }

    private sealed class FadeState(BridgeState? from, double idleTime, bool advancedThisTick, int channels, FadeState? inner)
    {
        public BridgeState? From { get; } = from;

        // The outgoing presentation, sampled each tick; allocated once per bridge install, not per frame.
        public Transform3D[] Pose { get; } = new Transform3D[channels];

        // A velocity blend still running when this one started: the outgoing presentation was itself blended (TR20).
        public FadeState? Inner { get; set; } = inner;

        // The outgoing bridge was still the current segment when this tick began, so its clock already advanced.
        public bool AdvancedThisTick { get; set; } = advancedThisTick;
        public double IdleTime { get; set; } = idleTime;
        public double Elapsed
        {
            get; set;
        }
    }

    private AnimationCatalogue? _animationCatalogue;
    private readonly Dictionary<AnimationEntry, NativeIdleClip> _idleClips = [];
    private NativeIdleClip? _idleClip;
    private BridgeState? _bridge, _outgoing;
    private FadeState? _fade;
    private IdleTarget? _submittedIdle, _nextIdleTarget;
    private Transform3D[] _nativeScratch = [];
    private Vector3 _presentedOffset, _observedOffset;
    private bool _anchorGround, _locomotionSwitchRequested, _locomotionSwitchAnnounce;

    /// <inheritdoc />
    public event Action<AnimationTransition>? TransitionStarted;

    /// <inheritdoc />
    public AnimationEntry? CurrentIdle => NativeIdleOwnsBody ? _idleClip?.Entry : (_bridge ?? _outgoing)?.Idle?.Clip.Entry ?? _submittedIdle?.Clip.Entry;

    /// <inheritdoc />
    public AnimationEntry? CurrentLocomotion
    {
        get; private set;
    }

    /// <inheritdoc />
    public AnimationCategory? DisplayedCategory => _animationCatalogue is null ? null
        : NativeIdleOwnsBody ? AnimationCategory.Idle
        // A failed hold presents a frozen generated pose: neither category is displayed.
        : Phase == "failed" ? null
        : (_bridge ?? _outgoing)?.Category
            ?? (_segment is not null || HasDestination ? AnimationCategory.Locomotion : null);

    /// <summary>Whether a native catalogue idle currently owns the body (TR13).</summary>
    public bool NativeIdleOwnsBody
    {
        get; private set;
    }

    /// <summary>Current native idle clip time in seconds; diagnostic only.</summary>
    internal double NativeIdleTime
    {
        get; private set;
    }

    /// <summary>The native idle writer installed on the reference skeleton; diagnostic only.</summary>
    internal NativeIdleModifier? IdleModifier
    {
        get; private set;
    }

    /// <summary>The playback plan of the active or outgoing bridge, if any; diagnostic only.</summary>
    internal IdleBridgePlan? ActiveBridgePlan => (_bridge ?? _outgoing)?.Plan;

    /// <summary>Whether a velocity blend from the previous presentation is running; diagnostic only.</summary>
    internal bool VelocityBlendActive => _fade is not null;

    /// <inheritdoc />
    public bool SetIdlePreference(string? id)
    {
        id = string.IsNullOrEmpty(id) ? null : id;
        if (_animationCatalogue is null || (id is not null && _animationCatalogue.Idle.Find(id) is null))
        {
            return false;
        }

        _animationCatalogue.Idle.Preference = id;
        _logger!.LogInformation("Idle preference on {Actor}: {Preference}; effective {Effective}",
            _actor!.Name, id ?? "(none)", _animationCatalogue.Idle.Effective);
        RequestEffectiveIdle();
        return true;
    }

    /// <inheritdoc />
    public bool SetLocomotionPreference(string? id)
    {
        id = string.IsNullOrEmpty(id) ? null : id;
        if (_animationCatalogue is null || (id is not null && _animationCatalogue.Locomotion.Find(id) is null))
        {
            return false;
        }

        _animationCatalogue.Locomotion.Preference = id;
        _locomotionSwitchRequested = !_animationCatalogue.Locomotion.Effective.Equals(CurrentLocomotion);
        _logger!.LogInformation("Locomotion preference on {Actor}: {Preference}; effective {Effective}",
            _actor!.Name, id ?? "(none)", _animationCatalogue.Locomotion.Effective);
        return true;
    }

    private void InstallNativeIdle(AnimationCatalogue catalogue)
    {
        _animationCatalogue = catalogue;
        CurrentLocomotion = catalogue.Locomotion.Effective;
        _nativeScratch = new Transform3D[_skeleton!.GetBoneCount()];
        IdleModifier = new NativeIdleModifier { Name = "NativeIdleModifier" };
        IdleModifier.Bind(_skeleton, _rig!.MappedTargetBones);
        _skeleton.AddChild(IdleModifier);
        int first = _skeleton.GetChildren().OfType<SkeletonModifier3D>().Where(m => m != IdleModifier)
            .Select(m => m.GetIndex()).DefaultIfEmpty(IdleModifier.GetIndex()).Min();
        _skeleton.MoveChild(IdleModifier, first);
        EnterNativeIdle(EffectiveIdleClip(), 0, null);
    }

    private NativeIdleClip EffectiveIdleClip() => IdleClip(_animationCatalogue!.Idle.Effective);

    private NativeIdleClip IdleClip(AnimationEntry entry)
    {
        if (!_idleClips.TryGetValue(entry, out NativeIdleClip? clip))
        {
            clip = new NativeIdleClip(entry, _animationCatalogue!.Idle.GetClip(entry.ID), _skeleton!);
            _idleClips.Add(entry, clip);
        }

        return clip;
    }

    private void RequestEffectiveIdle()
    {
        if (_unavailable || Phase == "failed" || HasDestination || _rig is null)
        {
            return;
        }

        AnimationEntry effective = _animationCatalogue!.Idle.Effective;
        AnimationEntry? presented = NativeIdleOwnsBody ? _idleClip?.Entry : (_segment is null ? _outgoing : _bridge)?.Idle?.Clip.Entry;
        AnimationEntry? pending = _pending ? _submittedIdle?.Clip.Entry : null;
        if (effective.Equals(pending))
        {
            // The replacement already being generated targets this idle.
            _idleTransitionRequested = false;
        }
        else if (effective.Equals(presented))
        {
            // Redirected back to what the body is already playing or bridging into: the pending replacement is
            // superseded and must never install (TR20).
            _idleTransitionRequested = false;
            CancelPendingIdleTransition();
        }
        else
        {
            _idleTransitionRequested = true;
        }
    }

    private void CancelPendingIdleTransition()
    {
        if (!_pending || _nativePending is not { IdleTarget: not null } packet)
        {
            return;
        }

        _worker!.CancelRequest();
        _pending = false;
        _nativePending = null;
        _submittedIdle = null;
        bool bridging = !NativeIdleOwnsBody && _segment is not null && _bridge is { Idle: not null };
        _idleTransitionActive = bridging;
        if (bridging)
        {
            Phase = _bridge!.Plan.Stationary ? "idle-transition" : "failure-to-idle";
        }

        _logger!.LogInformation("Idle request {Request} superseded by a redirect to the presented idle on {Actor}",
            packet.Ticket, _actor!.Name);
    }

    // Native→SOMA target window from the start of the clip (TR16), placed for an actor with the given yaw and position.
    private Transform3D[][] IdleTargetWindow(NativeIdleClip clip, Basis yaw, Vector3 position, float? modelHeading,
        bool stationary, out IdleTarget idle)
    {
        Transform3D actor = _actor!.GlobalTransform.Orthonormalized();
        Transform3D toSkeleton = actor.AffineInverse() * _skeleton!.GlobalTransform;
        Transform3D toSource = actor.AffineInverse() * _rig!.SourceGlobal;
        var target = new Transform3D[4][];
        if (modelHeading is { } heading)
        {
            var probe = new Transform3D[_pose.Length];
            ConvertIdleFrame(clip, 0, new Transform3D(yaw, position) * toSkeleton, probe);
            yaw = new Basis(Vector3.Up, Mathf.Wrap(heading - AnatomicalHeading(probe), -Mathf.Pi, Mathf.Pi)) * yaw;
        }

        var placement = new Transform3D(yaw, position);
        for (int f = 0; f < 4; f++)
        {
            target[f] = new Transform3D[_pose.Length];
            ConvertIdleFrame(clip, f / 30.0, placement * toSkeleton, target[f]);
        }

        Vector3 pinned = Ground(SomaMotionData.ModelToWorld.Inverse() * (placement * toSource).Origin);
        idle = new IdleTarget(clip, yaw, Ground(target[0][0].Origin) - pinned, stationary);
        return target;
    }

    // Locomotion→idle target (TR15–TR16): the effective idle's start, placed in the frame of an actor standing at the
    // destination with the requested facing. Destination facing is game-owned (NAV-001), so the actor finishes exactly
    // on it and the presented body shows the clip's own heading offset. The arrival bridge carries the hips into the
    // clip's offset.
    private Transform3D[][] ArrivalIdleWindow()
    {
        Vector3 forward = -Destination.Basis.Z;
        forward.Y = 0;
        var position = new Vector3(Destination.Origin.X, _actor!.GlobalPosition.Y, Destination.Origin.Z);
        Transform3D[][] target = IdleTargetWindow(EffectiveIdleClip(), YawFacing(forward), position, null, false,
            out IdleTarget idle);
        _nextIdleTarget = idle;
        return target;
    }

    private void ConvertIdleFrame(NativeIdleClip clip, double time, Transform3D skeletonGlobal, Transform3D[] soma)
    {
        for (int bone = 0; bone < _nativeScratch.Length; bone++)
        {
            _nativeScratch[bone] = _skeleton!.GetBoneRest(bone);
        }

        clip.Sample(time, _nativeScratch);
        _rig!.ConvertNative(_nativeScratch, skeletonGlobal, soma);
    }

    private static Basis YawFacing(Vector3 facing) => new(Vector3.Up, Mathf.Atan2(-facing.X, -facing.Z));

    private Basis ActorYaw()
    {
        Vector3 forward = -_actor!.GlobalBasis.Z;
        return YawFacing(new Vector3(forward.X, 0, forward.Z));
    }

    private void EnterNativeIdle(NativeIdleClip clip, double time, Basis? yaw)
    {
        if (yaw is { } basis)
        {
            // Game-owned actor yaw (ANIM TR4): the native clip plays in the actor frame, so the actor takes the facing
            // the bridge target was placed with. The presented body does not move.
            _actor!.GlobalBasis = basis;
        }

        NativeIdleOwnsBody = true;
        _idleClip = clip;
        NativeIdleTime = clip.Wrap(time);
        _bridge = null;
        _outgoing = null;
        _fade = null;
        _submittedIdle = null;
        _presentedOffset = Vector3.Zero;
        IdleModifier!.PlayIdle(clip, NativeIdleTime);
        _rig!.Relinquish();
        _logger?.LogDebug("Native idle {Entry} owns {Actor} from {Time:F3}s", clip.Entry, _actor!.Name, NativeIdleTime);
    }

    private void StepNativeIdle(double delta)
    {
        NativeIdleTime = _idleClip!.Wrap(NativeIdleTime + delta);
        IdleModifier!.Time = NativeIdleTime;
        // TR13: the retargeter never writes the body while a native idle owns it.
        _rig!.Relinquish();
        PlaybackObserved?.Invoke(0, null, 0);
        _record = true;
    }

    private void HoldPresentation(double delta)
    {
        if (NativeIdleOwnsBody)
        {
            StepNativeIdle(delta);
        }
        else
        {
            _rig!.Apply(_pose);
            _record = true;
        }
    }

    // Called after a native result is accepted and installed as the current segment.
    private void OnSegmentInstalled(NativePacket packet, Transform3D[][]? previous, double age)
    {
        // An early-submitted (overlap) segment continues the current playback; only a replacement interrupts it.
        BridgeState? interrupted = packet.Overlap ? null
            : _bridge is { } active && previous is not null && ReferenceEquals(active.Segment, previous) && !active.Finished ? active
            : _outgoing;
        bool fromIdle = NativeIdleOwnsBody;
        IdleTarget? idle = packet.IdleTarget;
        bool departure = idle is null && (fromIdle || interrupted is not null);
        bool announceSwitch = _locomotionSwitchAnnounce && idle is null && !departure;
        if (idle is null && !departure)
        {
            _bridge = null;
            _outgoing = null;
            _fade = null;
            if (IdleModifier!.Mode != NativeIdleMode.Off)
            {
                IdleModifier.Stop();
            }
            if (announceSwitch)
            {
                _locomotionSwitchAnnounce = false;
                Announce(AnimationCategory.Locomotion, CurrentLocomotion!, AnimationTransitionKind.LocomotionToLocomotion);
            }
            return;
        }

        BridgeSource source = fromIdle ? BridgeSource.NativeIdle
            : interrupted is null ? BridgeSource.Locomotion
            : interrupted.Plan.Stationary ? BridgeSource.StationaryBridge : BridgeSource.TravellingBridge;
        AnimationCategory to = idle is null ? AnimationCategory.Locomotion : AnimationCategory.Idle;
        AnimationCategory from = interrupted?.Category ?? (fromIdle ? AnimationCategory.Idle : AnimationCategory.Locomotion);
        // An arrival target placed at the destination travels even from a settled idle (a short successor destination).
        IdleBridgePlan plan = IdleBridgePlanner.Plan(source, from, to, travellingTarget: idle is { Stationary: false });
        if (idle is not null && idle.Stationary != plan.Stationary)
        {
            idle = idle with
            {
                Stationary = plan.Stationary
            };
        }

        var warp = BridgeTimeWarp.Create((_segment!.Length - 1) / 30.0,
            plan.EaseIn ? IdleEaseIn : 0, plan.EaseOut ? IdleEaseOut : 0);
        // Locomotion presents a zero offset, so this carries any residual offset of a bridge still settling.
        Vector3 start = fromIdle ? _observedOffset : _presentedOffset;
        AnimationEntry entry = idle?.Clip.Entry ?? CurrentLocomotion!;
        // Age is presentation time already elapsed since the realised context; play it on the warped clock.
        var bridge = new BridgeState(_segment, warp, plan, to, entry, idle, start)
        {
            Clock = age
        };
        _phaseTime = warp.SourceTime(age);
        // A blend still running into the interrupted presentation keeps running inside the new one, so the new blend
        // starts from the pose actually presented rather than from the raw outgoing segment.
        _fade = plan.VelocityBlend && TransitionBlendTime > 0
            ? new FadeState(interrupted, NativeIdleTime, interrupted is not null && previous is not null && ReferenceEquals(interrupted.Segment, previous),
                _pose.Length, interrupted is not null ? _fade : null)
            : null;
        _anchorGround = fromIdle || (interrupted?.Plan.Stationary ?? false);
        IdleModifier!.BeginBridge(fromIdle ? _idleClip : null, idle?.Clip);
        NativeIdleOwnsBody = false;
        _bridge = bridge;
        _outgoing = null;
        _submittedIdle = null;
        if (IdleBridgePlanner.Announces(interrupted?.Entry, entry))
        {
            Announce(to, entry, plan.Kind);
        }
        if (announceSwitch || (departure && _locomotionSwitchAnnounce))
        {
            _locomotionSwitchAnnounce = false;
        }
        _logger!.LogInformation("Bridge {Kind} to {Entry} on {Actor}: source {Source}, ease in {EaseIn}, ease out {EaseOut}, stationary {Stationary}, blend {Blend}, age {Age:F3}s",
            plan.Kind, entry, _actor!.Name, source, plan.EaseIn, plan.EaseOut, plan.Stationary, _fade is not null, age);
    }

    // A non-overlap result replacing a native idle or a still-playing bridge: an idle-target bridge or a departure whose
    // presentation kept moving during generation (TR20).
    private bool ReplacesMovingPresentation(NativePacket packet) => !packet.Overlap &&
        (NativeIdleOwnsBody || _outgoing is not null || (_bridge is { } active && ReferenceEquals(active.Segment, _segment) && !active.Finished));

    /// <summary>Bridge results discarded because their realised context aged past the limit; diagnostic only.</summary>
    internal int StaleBridgeRegenerations
    {
        get; private set;
    }

    private void Announce(AnimationCategory category, AnimationEntry entry, AnimationTransitionKind kind)
    {
        if (category == AnimationCategory.Locomotion)
        {
            CurrentLocomotion = entry;
        }

        TransitionStarted?.Invoke(new AnimationTransition(_actor!, category, entry, kind));
    }

    // Before generated segment playback advances: the warped bridge clock, or ordinary source time.
    private void AdvancePlayback(double delta)
    {
        if (_bridge is { } bridge && ReferenceEquals(bridge.Segment, _segment))
        {
            bridge.Clock += delta;
            _phaseTime = bridge.Warp.SourceTime(bridge.Clock);
        }
        else
        {
            _phaseTime += delta;
        }
    }

    // After the current segment is sampled into _pose: velocity blend from the outgoing presentation.
    private void BlendFromOutgoing(double delta)
    {
        if (_fade is not { } fade)
        {
            return;
        }

        float weight = SampleOutgoing(fade, delta);
        for (int j = 0; j < _pose.Length; j++)
        {
            _pose[j] = SomaMotionData.Blend(fade.Pose[j], _pose[j], weight);
        }

        if (weight >= 1)
        {
            _fade = null;
        }
    }

    // Advances a blend and samples its outgoing presentation into its pose, including any inner blend still running in
    // that presentation. Returns the blend weight towards the incoming motion.
    private float SampleOutgoing(FadeState fade, double delta)
    {
        fade.Elapsed += delta;
        if (fade.From is { } from)
        {
            if (!fade.AdvancedThisTick)
            {
                from.Clock += delta;
            }

            fade.AdvancedThisTick = false;
            SomaMotionData.Sample(from.Segment, from.Warp.SourceTime(from.Clock), fade.Pose);
        }
        else
        {
            fade.IdleTime = _idleClip!.Wrap(fade.IdleTime + delta);
            ConvertIdleFrame(_idleClip, fade.IdleTime, _skeleton!.GlobalTransform, fade.Pose);
        }

        if (fade.Inner is { } inner)
        {
            float innerWeight = SampleOutgoing(inner, delta);
            for (int j = 0; j < fade.Pose.Length; j++)
            {
                fade.Pose[j] = SomaMotionData.Blend(inner.Pose[j], fade.Pose[j], innerWeight);
            }

            if (innerWeight >= 1)
            {
                fade.Inner = null;
            }
        }

        return TransitionBlend.Weight(fade.Elapsed, TransitionBlendTime);
    }

    // A destination interrupting a bridge keeps the bridge moving until the replacement arrives (TR20). A velocity
    // blend still running into that bridge keeps running, so the presented pose does not jump to the raw bridge.
    private void RetainOutgoingBridge()
    {
        if (_bridge is { } bridge && ReferenceEquals(bridge.Segment, _segment) && !bridge.Finished)
        {
            _outgoing = bridge;
        }
        else
        {
            _fade = null;
        }

        _bridge = null;
    }

    private void StepOutgoingBridge(double delta)
    {
        BridgeState outgoing = _outgoing!;
        outgoing.Clock += delta;
        SomaMotionData.Sample(outgoing.Segment, outgoing.Warp.SourceTime(outgoing.Clock), _pose);
        if (outgoing.Finished && outgoing.Idle is { } idle)
        {
            EnterNativeIdle(idle.Clip, outgoing.Clock - outgoing.Warp.PlaybackDuration, idle.Stationary ? null : idle.Yaw);
        }
    }

    // When the current segment reaches its end.
    private void FinishBridgeSegment()
    {
        if (_bridge is { } bridge && ReferenceEquals(bridge.Segment, _segment))
        {
            if (bridge.Idle is { } idle)
            {
                EnterNativeIdle(idle.Clip, Math.Max(0, bridge.Clock - bridge.Warp.PlaybackDuration), idle.Stationary ? null : idle.Yaw);
            }
            else
            {
                _bridge = null;
                if (IdleModifier!.Mode == NativeIdleMode.Bridge)
                {
                    IdleModifier.Stop();
                }
            }
        }
    }

    // Planar hips offset from the pinned position for the presented bridge, and whether the actor stays in place.
    private Vector3 UpdateBridgeOffset(out bool stationary, out bool bridging)
    {
        BridgeState? bridge = _bridge is { } active && ReferenceEquals(active.Segment, _segment) ? active : _segment is null ? _outgoing : null;
        bridging = bridge is not null;
        stationary = bridge?.Plan.Stationary ?? false;
        if (bridge is null)
        {
            return Vector3.Zero;
        }

        float weight = TransitionBlend.Weight(bridge.Clock, bridge.Warp.PlaybackDuration);
        if (IdleModifier!.Mode == NativeIdleMode.Bridge)
        {
            IdleModifier.BridgeWeight = weight;
        }

        return stationary ? Ground(_pose[0].Origin) - _rig!.PinnedModelGround()
            : bridge.StartOffset.Lerp(bridge.Idle?.EndOffset ?? Vector3.Zero, weight);
    }

    private void ObserveOffset() => _observedOffset = Ground(_observed[0].Origin) - _rig!.PinnedModelGround();

    private void ApplyLocomotionSwitch()
    {
        if (!_locomotionSwitchRequested || _finiteTurn is not null)
        {
            return;
        }

        _locomotionSwitchRequested = false;
        AnimationEntry entry = _animationCatalogue!.Locomotion.Effective;
        ReviewedRoleSampler sampler = _animationCatalogue.Locomotion.CreateSampler(_data!, entry.ID);
        _ = sampler.TurnReference("left_turn");
        _ = sampler.TurnReference("right_turn");
        _reviewed = sampler;
        // ANIM-002 TR4: results sampled from the abandoned variant are stale and must not be installed.
        _roleRevision++;
        _locomotionSwitchAnnounce = HasDestination;
        CurrentLocomotion = entry;

        _logger!.LogInformation("Locomotion variant on {Actor} switched to {Entry}", _actor!.Name, entry);
    }
}
