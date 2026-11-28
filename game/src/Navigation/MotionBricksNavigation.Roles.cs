using AlleyCat.Motion;
using Godot;

namespace AlleyCat.Navigation;

public partial class MotionBricksNavigation
{
    /// <inheritdoc/>
    [ExportGroup("Natural Role Selection")]
    [Export] public float PositioningEnterDistance { get; set; } = 1.5f;
    /// <inheritdoc/>
    [Export] public float PositioningExitDistance { get; set; } = 1.75f;
    /// <inheritdoc/>
    [Export] public float PositioningFacingDegrees { get; set; } = 35;
    /// <inheritdoc/>
    [Export] public float PositioningLateralBandDegrees { get; set; } = 20;
    /// <inheritdoc/>
    [Export] public float PositioningHysteresisDegrees { get; set; } = 10;
    /// <inheritdoc/>
    [Export] public float PositioningCrossingDistance { get; set; } = .75f;

    internal sealed record RoleSubmission(long Revision, NavigationRolePolicy.Decision Decision,
        string SelectedRole, SourcePhaseClock.Selection Source, long RouteRevision, long Generation);
    internal NavigationRolePolicy.Decision? RoleDecision
    {
        get; private set;
    }
    internal RoleSubmission? SubmittedRole
    {
        get; private set;
    }
    private long _roleRevision;
    private bool _positioningTranslationSubmitted, _positioningTurnSubmitted;
    internal sealed record PositioningPlacement(double NominalLead, double Lead, float FitScale, float TranslationYaw);
    internal PositioningPlacement? PositioningPlacementIntent
    {
        get; private set;
    }

    private void ResetRoleDecision()
    {
        _roleRevision++;
        RoleDecision = null;
        SubmittedRole = null;
        _positioningTranslationSubmitted = _positioningTurnSubmitted = false;
        PositioningPlacementIntent = null;
    }

    private void RefreshRoleDecision(Transform3D[][] context)
    {
        if (_reviewed is null || CurrentRouteSnapshot is not { } route || !_intent.HasValidSample)
        {
            return;
        }

        float heading = AnatomicalHeading(context[3]) + Mathf.Pi;
        Vector3 forward = new(Mathf.Sin(heading), 0, Mathf.Cos(heading));
        Vector3 left = new(Mathf.Cos(heading), 0, -Mathf.Sin(heading));
        Vector3 displacement = Ground(Destination.Origin - _actor!.GlobalPosition);
        Vector3 motion = SomaMotionData.ModelToWorld * Ground(context[3][0].Origin - context[2][0].Origin);
        Vector3 facing = -Destination.Basis.Z;
        float error = Mathf.Wrap(Mathf.Atan2(facing.X, facing.Z) - heading, -Mathf.Pi, Mathf.Pi);
        var input = new NavigationRolePolicy.Input(displacement.Dot(forward), displacement.Dot(left),
            _intent.RemainingPathDistance, displacement.Length(), error, motion.Dot(forward), motion.Dot(left),
            _intent.PositionReached, Math.Abs(error) <= Mathf.DegToRad(FacingToleranceDegrees), route.DestinationRequestGeneration, route.RouteRevision);
        NavigationRolePolicy.Decision next = _finiteTurn is { } plan
            ? new(plan.Role, true, plan.FinishedCues ? "finite-terminal-facing-finish" : "finite-terminal-facing-plan", input)
            : NavigationRolePolicy.Select(input,
            new(PositioningEnterDistance, PositioningExitDistance, PositioningFacingDegrees, PositioningLateralBandDegrees,
                PositioningHysteresisDegrees, PositioningCrossingDistance, Math.Max(0, ShortMoveDistance)), RoleDecision);
        if (RoleDecision is null || next.Role != RoleDecision.Role || next.Positioning != RoleDecision.Positioning ||
            next.Inputs.Generation != RoleDecision.Inputs.Generation)
        {
            _roleRevision++;
        }

        RoleDecision = next;
    }

    private bool RoleStillCurrent(RoleSubmission snapshot) => snapshot.Revision == _roleRevision &&
        snapshot.Generation == CurrentRouteSnapshot?.DestinationRequestGeneration && snapshot.SelectedRole == _reviewed!.Role &&
        snapshot.Source.Revision == _reviewed.Selection.Revision && snapshot.Source.Key == _reviewed.Selection.Key;

    private void SnapshotSubmittedRole()
    {
        SubmittedRole = RoleDecision is { } decision && CurrentRouteSnapshot is { } route
            ? new(_roleRevision, decision, _reviewed!.Role, _reviewed.Selection, route.RouteRevision, route.DestinationRequestGeneration) : null;
    }

    /// <summary>Selected-role positioning, not a forward arc with a lateral source label.</summary>
    private bool SubmitPositioning(Transform3D[][] context, Vector3 start, Vector3 corridorEnd)
    {
        if (RoleDecision is not { Positioning: true } decision || _reviewed is null)
        {
            return false;
        }

        const double horizon = 49.0 / 30;
        float heading = AnatomicalHeading(context[3]) + Mathf.Pi;
        bool turn = decision.Role.EndsWith("_turn", StringComparison.Ordinal);
        if (decision.Inputs.PositionReached && turn)
        {
            _finiteTurn = new(_reviewed.TurnReference(decision.Role), heading, decision.Inputs.FacingError, _reviewed.RoleSourceSHA256(decision.Role));
            _finiteRequest = CurrentStatus.ActiveRequest;
            _finiteRouteRevision = CurrentRouteSnapshot!.RouteRevision;
            _finiteRevision++;
            return SubmitFiniteTurn(context);
        }
        float rate = _reviewed.TurnReferenceAngularVelocity(decision.Inputs.FacingError > 0);
        float distance = Ground(corridorEnd - start).Length();
        bool final = NativeDestinationLeg && corridorEnd.DistanceTo(Destination.Origin) <= .0001f;
        _ = _reviewed.SetIntent(decision.Role, ObservationTime, horizon);
        // A translation cannot consume the turn's required window, nor a turn the translation's.
        bool selectedKindSubmitted = turn ? _positioningTurnSubmitted : _positioningTranslationSubmitted;
        if (selectedKindSubmitted && final && distance <= (_reviewed.ReferenceSpeed * NormalisedSpeed * horizon) + .001 &&
            Math.Abs(decision.Inputs.FacingError) <= Math.Abs(rate) * horizon)
        {
            return SubmitPositioningIdle(context);
        }

        if (decision.Role.Length == 0)
        {
            return SubmitPositioningIdle(context);
        }

        float speed = _reviewed.ReferenceSpeed * NormalisedSpeed;
        Transform3D[][] target = _reviewed.Targets(ObservationTime, horizon, Basis.Identity, Vector3.Zero, NormalisedSpeed);
        float modelHeading = heading - Mathf.Pi;
        if (turn)
        {
            modelHeading += (float)Math.Clamp(decision.Inputs.FacingError, -Math.Abs(rate) * horizon, Math.Abs(rate) * horizon);
        }

        Basis rotation = new(Vector3.Up, modelHeading - AnatomicalHeading(target[0]));
        Vector3 direction = Ground(corridorEnd - start).Normalized();
        PositioningPlacementIntent = null;
        Vector3[]? translated = null;
        if (!turn)
        {
            // Reference speed places the window's horizon anchor; it does not replace local source velocity.
            // Targets already applied requested translation scale once. Preserve those four offsets together.
            Vector3 sourceOrigin = Ground(target[0][0].Origin);
            Vector3 chord = Ground(target[3][0].Origin) - sourceOrigin;
            Vector3 modelDirection = SomaMotionData.ModelToWorld.Inverse() * direction;
            float yaw = chord.LengthSquared() > 0
                ? Mathf.Atan2(modelDirection.X, modelDirection.Z) - Mathf.Atan2(chord.X, chord.Z) : 0;
            Basis travel = new(Vector3.Up, yaw);
            var offsets = new Vector3[4];
            double min = 0, max = 0;
            for (int i = 0; i < 4; i++)
            {
                offsets[i] = travel * (Ground(target[i][0].Origin) - sourceOrigin);
                double along = offsets[i].Dot(modelDirection);
                min = Math.Min(min, along);
                max = Math.Max(max, along);
            }
            // Fit by one common scale/placement, never per-frame endpoint clamping or constant-speed resampling.
            float fit = max - min > distance ? MathF.BitDecrement((float)(distance / (max - min))) : 1;
            fit = Math.Max(0, fit);
            double nominal = speed * horizon;
            double lead = Math.Clamp(nominal, -min * fit, distance - (max * fit));
            PositioningPlacementIntent = new(nominal, lead, fit, yaw);
            translated = new Vector3[4];
            Vector3 anchor = (SomaMotionData.ModelToWorld.Inverse() * start) + (modelDirection * (float)lead);
            for (int i = 0; i < 4; i++)
            {
                translated[i] = anchor + (offsets[i] * fit);
            }
        }
        for (int i = 0; i < 4; i++)
        {
            target[i][0].Basis = rotation * target[i][0].Basis;
            Vector3 position = turn
                ? (SomaMotionData.ModelToWorld.Inverse() * start) + (rotation * Ground(target[i][0].Origin))
                : translated![i];
            position.Y = target[i][0].Origin.Y;
            target[i][0].Origin = position;
        }
        _arrival = false;
        SubmitPlacedPositioning(context, target, speed, rate);
        if (turn)
        {
            _positioningTurnSubmitted = true;
        }
        else
        {
            _positioningTranslationSubmitted = true;
        }
        return true;
    }

    private bool SubmitPositioningIdle(Transform3D[][] context)
    {
        if (!NativeDestinationLeg)
        {
            return false;
        }
        PositioningPlacementIntent = null;
        _ = _reviewed!.SetIntent("", ObservationTime);
        Transform3D[][] target = ArrivalIdleWindow();
        _arrival = true;
        SubmitPlacedPositioning(context, target, 0, 0);
        return true;
    }

    private void SubmitPlacedPositioning(Transform3D[][] context, Transform3D[][] target, float speed, float rate)
    {
        SourceRateCurveIntent = new(AnatomicalHeading(context[3]) + Mathf.Pi, RoleDecision!.Inputs.FacingError, speed, rate, _arrival,
            [49.0 / 30, 50.0 / 30, 51.0 / 30, 52.0 / 30], [.. target.Select(p => AnatomicalHeading(p) + Mathf.Pi)],
            [.. target.Select(p => RefusalPoint(SomaMotionData.ModelToWorld * p[0].Origin))]);
        SubmitTarget(context, target);
    }
}
