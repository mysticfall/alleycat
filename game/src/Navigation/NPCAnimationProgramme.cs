using AlleyCat.Control.Locomotion;
using Godot;

#pragma warning disable CS1591, IDE0032, IDE0072

namespace AlleyCat.Navigation;

public enum NPCAnimationStage
{
    Idle,
    Entry,
    ShortBout,
    SustainedTravel,
    Exit,
    Settlement,
    EndpointHold,
    Complete,
    Pivot,
    SideStep,
}

public enum NPCAnimationPhaseLane
{
    Left,
    Right,
}

public enum NPCAnimationEvent
{
    None,
    StartEntry,
    StartShortBout,
    StartPivot,
    StartSideStep,
    StartTravel,
    StartExit,
    StartSettlement,
    StartEndpointHold,
    Complete,
}

public readonly record struct NPCAnimationFrame(
    NPCAnimationStage Stage,
    NPCAnimationPhaseLane PhaseLane,
    NPCAnimationEvent Event,
    StringName State,
    Vector2 BlendPoint,
    float PlaybackRate,
    Vector2 DesiredLocalVelocity,
    float DesiredYawRate,
    bool TerminalCommitted);

/// <summary>Retained event-driven animation programme for one NPC destination request.</summary>
public sealed class NPCAnimationProgramme
{
    // Godot gameplay transforms use 32-bit floats. At NAV-001's metre-scale routes, 1e-4 remains
    // comfortably above representational noise while being 400 times tighter than arrival policy.
    public const float NumericalConvergenceThreshold = 0.0001f;
    public const int StableConvergenceFrameCount = 4;
    private int _stableFrames;
    private NPCShortBoutSplicePlan? _shortBout;
    private NPCTurnInPlacePlan? _turnInPlace;
    private NPCSideStepPlan? _sideStep;
    private NPCTurnInPlacePlan? _compositionPivot;
    private int _sideStepChainRemaining;
    private int _pivotChainRemaining;
    private bool _sustainedTravelRequested;

    public NPCAnimationStage Stage
    {
        get; private set;
    }
    public NPCAnimationPhaseLane PhaseLane
    {
        get; private set;
    }
    public int EntryCount
    {
        get; private set;
    }
    public int ExitCount
    {
        get; private set;
    }
    public int DomainHandoffCount
    {
        get; private set;
    }
    public int IdleCrossingCount
    {
        get; private set;
    }
    public bool TerminalCommitted
    {
        get; private set;
    }
    public bool IsBackwardBout
    {
        get; private set;
    }

    public NPCShortBoutSplicePlan? ShortBout => _shortBout;

    public NPCTurnInPlacePlan? TurnInPlace => _turnInPlace;

    public NPCSideStepPlan? SideStep => _sideStep;

    /// <summary>
    /// Remaining side-step clips in the active chain, counting the currently playing one. Chaining:
    /// intermediate repetitions replay the same confined clip; the final repetition carries the dispersed remainder.
    /// </summary>
    public int SideStepChainRemaining => _sideStepChainRemaining;

    /// <summary>Remaining pivot clips in the active chain, counting the currently playing one.</summary>
    public int PivotChainRemaining => _pivotChainRemaining;

    /// <summary>Whether NPCTravel was published but has not yet been evaluated by the AnimationTree.</summary>
    public bool IsSustainedTravelHandoffPending => _sustainedTravelRequested;

    /// <summary>
    /// Commits the requested Entry→Travel hand-off only after the AnimationTree has evaluated NPCTravel. The start
    /// event may be published one producer/consumer interval earlier, but it must not create a live phase clock or
    /// release-occurrence identity until the target child is real.
    /// </summary>
    public bool ConfirmSustainedTravelEvaluated(bool sustainedStateEvaluated)
    {
        if (!_sustainedTravelRequested || !sustainedStateEvaluated)
        {
            return false;
        }

        if (Stage != NPCAnimationStage.SustainedTravel)
        {
            throw new InvalidOperationException("Only a pending entry-to-travel hand-off can be confirmed.");
        }

        _sustainedTravelRequested = false;
        return true;
    }

    public void ConfigureShortBout(NPCShortBoutSplicePlan plan)
    {
        if (Stage != NPCAnimationStage.Idle)
        {
            throw new InvalidOperationException("A short bout can only begin from evaluated idle.");
        }

        _shortBout = plan;
        IsBackwardBout = plan.EntryCapability.IsBackward();
        PhaseLane = plan.EntryCapability.Side() == StandingLocomotionAnatomicalSide.Right
            ? NPCAnimationPhaseLane.Right
            : NPCAnimationPhaseLane.Left;
    }

    public void SelectExitLane(NPCAnimationPhaseLane lane)
    {
        if (Stage != NPCAnimationStage.SustainedTravel || TerminalCommitted)
        {
            throw new InvalidOperationException("NPC exit lane can only be selected from uninterrupted sustained travel.");
        }

        PhaseLane = lane;
    }

    public void ConfigureTurnInPlace(NPCTurnInPlacePlan plan)
    {
        if (Stage != NPCAnimationStage.Idle)
        {
            throw new InvalidOperationException("A turn-in-place manoeuvre can only begin from evaluated idle.");
        }

        _turnInPlace = plan;
        _pivotChainRemaining = plan.ChainedPivotCount;
    }

    /// <summary>
    /// Configures the confined side-step chain, optionally composing a follow-up pivot chain that plays after the
    /// steps within the same programme (step chain first in
    /// the actor's current frame, then rotate to the terminal yaw). The composed pivots carry authored yaw only;
    /// settlement still owns endpoint convergence.
    /// </summary>
    public void ConfigureSideStep(NPCSideStepPlan plan, NPCTurnInPlacePlan? compositionPivot = null)
    {
        if (Stage != NPCAnimationStage.Idle)
        {
            throw new InvalidOperationException("A side-step manoeuvre can only begin from evaluated idle.");
        }

        _sideStep = plan;
        _compositionPivot = compositionPivot;
        _sideStepChainRemaining = plan.ChainedStepCount;
    }

    /// <summary>
    /// Moves a short bout whose endpoint residual is within the confined absorption capacity into settlement so the
    /// existing no-crossing convergence owns endpoint convergence instead of the endpoint hold.
    /// </summary>
    public void BeginResidualSettlement()
    {
        if (Stage != NPCAnimationStage.ShortBout)
        {
            throw new InvalidOperationException("Short-bout residual settlement can only begin from an active short bout.");
        }

        Stage = NPCAnimationStage.Settlement;
        _stableFrames = 0;
    }

    public NPCAnimationFrame CompleteNoMovement()
    {
        if (Stage != NPCAnimationStage.Idle)
        {
            throw new InvalidOperationException("A no-movement request can only complete from idle.");
        }

        Stage = NPCAnimationStage.Complete;
        _stableFrames = StableConvergenceFrameCount;
        return new(
            Stage,
            PhaseLane,
            NPCAnimationEvent.Complete,
            StateFor(NPCAnimationEvent.Complete),
            Vector2.Zero,
            0f,
            Vector2.Zero,
            0f,
            TerminalCommitted);
    }

    public void Reset()
    {
        Stage = NPCAnimationStage.Idle;
        PhaseLane = NPCAnimationPhaseLane.Left;
        IsBackwardBout = false;
        TerminalCommitted = false;
        _stableFrames = 0;
        _shortBout = null;
        _turnInPlace = null;
        _sideStep = null;
        _compositionPivot = null;
        _sideStepChainRemaining = 0;
        _pivotChainRemaining = 0;
        _sustainedTravelRequested = false;
        EntryCount = 0;
        ExitCount = 0;
        DomainHandoffCount = 0;
        IdleCrossingCount = 0;
    }

    /// <summary>
    /// Completes a terminal-committed finite exit directly. The owned f2i window consumed the clip's final
    /// interval and closed the hard motion boundary, so the settlement polish is replaced by the terminal
    /// completion publication that travels the state machine off the finite clip.
    /// </summary>
    public bool TryCompleteTerminalExit(out NPCAnimationFrame completionFrame)
    {
        if (Stage != NPCAnimationStage.Exit || !TerminalCommitted)
        {
            completionFrame = default;
            return false;
        }

        Stage = NPCAnimationStage.Complete;
        completionFrame = new(
            Stage,
            PhaseLane,
            NPCAnimationEvent.Complete,
            new StringName("NPCIdle"),
            Vector2.Zero,
            NPCFiniteTransitionCadence.SourceSecondsPerWallSecond,
            Vector2.Zero,
            0f,
            TerminalCommitted);
        return true;
    }

    public NPCAnimationFrame Step(
        Transform3D actor,
        NPCNavigationRoutePlan route,
        float progress,
        bool finiteStateCompleted,
        bool executeTerminalExit,
        bool terminalPhaseOneObserved = true)
    {
        Vector3 actorPosition = Horizontal(actor.Origin);
        float remaining = Math.Max(route.TotalLength - progress, 0f);
        Vector3 target = route.Sample(Math.Min(progress + 0.45f, route.TotalLength), out _);
        NPCNavigationSemanticFrame actorFrame = new(actor.Basis, actor.Basis);
        Vector2 localTarget = actorFrame.WorldRouteToActorSemantic(target - actorPosition);
        float actorYaw = NPCNavigationRoutePlan.YawFromDirection(actor.Basis.Orthonormalized() * Vector3.Forward);
        Vector3 steeringDirection = target - actorPosition;
        float routeYaw = steeringDirection.IsZeroApprox()
            ? route.DesiredRouteYaw(Math.Min(progress + 0.35f, route.TotalLength))
            : NPCNavigationRoutePlan.YawFromDirection(steeringDirection);
        float terminalYawError = NPCNavigationRoutePlan.WrapAngle(route.TerminalYaw - actorYaw);
        float routeYawError = NPCNavigationRoutePlan.WrapAngle(routeYaw - actorYaw);
        float endpointDistance = actorPosition.DistanceTo(route.Endpoint);

        NPCAnimationEvent programmeEvent = NPCAnimationEvent.None;
        if (Stage == NPCAnimationStage.Idle)
        {
            if (_shortBout is not null)
            {
                Stage = NPCAnimationStage.ShortBout;
                EntryCount++;
                ExitCount++;
                programmeEvent = NPCAnimationEvent.StartShortBout;
            }
            else if (_turnInPlace is { } turnInPlace)
            {
                if (turnInPlace.PlaysPivotClip)
                {
                    Stage = NPCAnimationStage.Pivot;
                    EntryCount++;
                    programmeEvent = NPCAnimationEvent.StartPivot;
                }
                else
                {
                    Stage = NPCAnimationStage.Settlement;
                    programmeEvent = NPCAnimationEvent.StartSettlement;
                }
            }
            else if (_sideStep is { })
            {
                Stage = NPCAnimationStage.SideStep;
                EntryCount++;
                programmeEvent = NPCAnimationEvent.StartSideStep;
            }
            else
            {
                // NAV-001 bout-family decision: the raw bearing test alone mis-classifies destinations behind
                // the actor whose terminal pose faces ALONG the route (the reported behind-left big-yaw case:
                // target local (−1.00, −1.14), terminal yaw 2.42 ≈ the route bearing). The bout then ran
                // backward while the yaw steering swept the actor ~180°, flipping the sustained blend's
                // hemisphere mid-bout (measured (0,−0.93) → (0.49,0.53) through the interior mixes), and the
                // bout-scoped backward exit family plus the armed walk-point pin then walked the actor 0.57 m
                // back away from the target before a large feet-frozen settlement slide. When the terminal yaw
                // faces along the route direction (cos > 0.35), the bout is
                // forward regardless of the transient bearing — the actor arcs around and walks forward into
                // the terminal pose, keeping the entry, blend demand, armed pin and exit family coherent.
                float terminalRouteAlignment = Mathf.Cos(NPCNavigationRoutePlan.WrapAngle(route.TerminalYaw - routeYaw));
                bool terminalFacesAlongRoute = terminalRouteAlignment > 0.35f;
                IsBackwardBout = localTarget.Y < -Mathf.Abs(localTarget.X) * 0.35f && !terminalFacesAlongRoute;
                PhaseLane = localTarget.X > 0f ? NPCAnimationPhaseLane.Right : NPCAnimationPhaseLane.Left;
                Stage = NPCAnimationStage.Entry;
                EntryCount++;
                programmeEvent = NPCAnimationEvent.StartEntry;
            }
        }
        else if (Stage == NPCAnimationStage.Entry && finiteStateCompleted && !_sustainedTravelRequested)
        {
            _sustainedTravelRequested = true;
            Stage = NPCAnimationStage.SustainedTravel;
            programmeEvent = NPCAnimationEvent.StartTravel;
        }

        if (!TerminalCommitted
            && Stage == NPCAnimationStage.SustainedTravel
            && executeTerminalExit)
        {
            TerminalCommitted = true;
            Stage = NPCAnimationStage.Exit;
            ExitCount++;
            programmeEvent = NPCAnimationEvent.StartExit;
        }
        else if (Stage == NPCAnimationStage.Exit && finiteStateCompleted && terminalPhaseOneObserved)
        {
            Stage = NPCAnimationStage.Settlement;
            programmeEvent = NPCAnimationEvent.StartSettlement;
        }
        else if (Stage == NPCAnimationStage.ShortBout && finiteStateCompleted)
        {
            Stage = NPCAnimationStage.EndpointHold;
            programmeEvent = NPCAnimationEvent.StartEndpointHold;
            _stableFrames = 0;
        }
        else if (Stage == NPCAnimationStage.Pivot && finiteStateCompleted)
        {
            if (_pivotChainRemaining > 1)
            {
                // Replay the same authored pivot clip; no synthetic rotation is introduced
                // and the convergence remainder still resolves through bounded settlement yaw.
                _pivotChainRemaining--;
                EntryCount++;
                programmeEvent = NPCAnimationEvent.StartPivot;
            }
            else
            {
                Stage = NPCAnimationStage.Settlement;
                programmeEvent = NPCAnimationEvent.StartSettlement;
            }
        }
        else if (Stage == NPCAnimationStage.SideStep && finiteStateCompleted)
        {
            if (_sideStepChainRemaining > 1)
            {
                // Replay the loopable confined clip with zero crossfade. Intermediate
                // repetitions play as pure full clips; only the final repetition carries the dispersed remainder.
                _sideStepChainRemaining--;
                EntryCount++;
                programmeEvent = NPCAnimationEvent.StartSideStep;
            }
            else if (_compositionPivot is { } pivot)
            {
                // Beyond-capacity confined composition finishes the side-step chain first, then rotates toward the
                // terminal yaw; settlement afterwards converges the residual yaw and the exact final landing.
                _turnInPlace = pivot;
                _compositionPivot = null;
                _pivotChainRemaining = pivot.ChainedPivotCount;
                Stage = NPCAnimationStage.Pivot;
                EntryCount++;
                programmeEvent = NPCAnimationEvent.StartPivot;
            }
            else
            {
                Stage = NPCAnimationStage.Settlement;
                programmeEvent = NPCAnimationEvent.StartSettlement;
            }
        }

        bool positionReached = endpointDistance <= NumericalConvergenceThreshold;
        bool facingReached = Mathf.Abs(terminalYawError) <= NumericalConvergenceThreshold;
        if (Stage is NPCAnimationStage.Settlement or NPCAnimationStage.EndpointHold)
        {
            _stableFrames = positionReached && facingReached ? _stableFrames + 1 : 0;
            if (_stableFrames >= StableConvergenceFrameCount)
            {
                Stage = NPCAnimationStage.Complete;
                programmeEvent = NPCAnimationEvent.Complete;
            }
        }

        Vector2 desiredVelocity;
        float desiredYawRate;
        if (Stage == NPCAnimationStage.Exit)
        {
            // The production vector reservation owns planar convergence during Exit; the programme
            // contributes only the authored-yaw convergence term.
            desiredVelocity = Vector2.Zero;
            desiredYawRate = Mathf.Clamp(terminalYawError * 8f, -0.7f, 0.7f);
        }
        else if (Stage is NPCAnimationStage.Pivot
            or NPCAnimationStage.SideStep
            or NPCAnimationStage.Settlement
            or NPCAnimationStage.EndpointHold
            or NPCAnimationStage.Complete)
        {
            // Pivots author yaw only and side steps author lateral planar only; their Root translation/yaw is
            // package-zero in the absorbed degree of freedom and residuals converge in settlement.
            desiredVelocity = Vector2.Zero;
            desiredYawRate = 0f;
        }
        else
        {
            float speed = Mathf.Clamp(remaining / 0.8f, 0.35f, 1f) * 0.93f;
            desiredVelocity = localTarget.IsZeroApprox() ? Vector2.Zero : localTarget.Normalized() * speed;
            desiredYawRate = Mathf.Clamp(routeYawError * 6f, -1.2f, 1.2f);
        }

        float rate = NPCFiniteTransitionCadence.SourceSecondsPerWallSecond;
        return new(
            Stage,
            PhaseLane,
            programmeEvent,
            StateFor(programmeEvent),
            desiredVelocity,
            rate,
            desiredVelocity,
            desiredYawRate,
            TerminalCommitted);
    }

    private StringName StateFor(NPCAnimationEvent programmeEvent) => programmeEvent switch
    {
        NPCAnimationEvent.StartEntry => new(IsBackwardBout
            ? PhaseLane == NPCAnimationPhaseLane.Left ? "NPCI2BL" : "NPCI2BR"
            : PhaseLane == NPCAnimationPhaseLane.Left ? "NPCI2FL" : "NPCI2FR"),
        NPCAnimationEvent.StartShortBout => new(_shortBout!.Value.EntryCapability switch
        {
            StandingLocomotionCapability.EntryForwardRight => "NPCShortFR",
            StandingLocomotionCapability.EntryForwardLeft => "NPCShortFL",
            StandingLocomotionCapability.EntryBackwardRight => "NPCShortBR",
            StandingLocomotionCapability.EntryBackwardLeft => "NPCShortBL",
            _ => throw new InvalidOperationException("Unknown package-approved short-bout entry."),
        }),
        NPCAnimationEvent.StartPivot => new(_turnInPlace!.Value.Kind == NPCTurnInPlaceKind.PivotLeft
            ? "NPCPivotL"
            : "NPCPivotR"),
        NPCAnimationEvent.StartSideStep => new(_sideStep!.Value.Kind == NPCSideStepKind.StepLeft
            ? "NPCSideStepL"
            : "NPCSideStepR"),
        NPCAnimationEvent.StartTravel => new("NPCTravel"),
        NPCAnimationEvent.StartExit => new(IsBackwardBout
            ? PhaseLane == NPCAnimationPhaseLane.Left ? "NPCB2IL" : "NPCB2IR"
            : PhaseLane == NPCAnimationPhaseLane.Left ? "NPCF2IL" : "NPCF2IR"),
        NPCAnimationEvent.Complete => new("NPCIdle"),
        NPCAnimationEvent.StartEndpointHold => new("NPCIdle"),
        NPCAnimationEvent.StartSettlement => new StringName(),
        NPCAnimationEvent.None => new StringName(),
        _ => new StringName(),
    };

    private static Vector3 Horizontal(Vector3 value) => new(value.X, 0f, value.Z);
}
