using System.Collections.Immutable;
using AlleyCat.Control.Locomotion;
using Godot;

namespace AlleyCat.Tests.Control;

/// <summary>Test-owned declaration for travel seam and release-occurrence contracts.</summary>
internal static class SyntheticTravelFixture
{
    public const string SourceIdentity = "ember-player";
    public const string CatalogueProvenance = "ember-catalogue";
    public const string WalkRole = "wf";
    public const string ReverseRole = "wb";
    public const string LeftExitRole = "f2i_l";
    public const string RightExitRole = "f2i_r";
    public const double CycleSeconds = 1d;
    public const double ReleaseTimeSeconds = 0.5d;

    public static StandingLocomotionClip CreateClip(double duration, double? leftRelease, double? rightRelease)
    {
        ImmutableDictionary<string, double>.Builder events = ImmutableDictionary.CreateBuilder<string, double>(StringComparer.Ordinal);
        ImmutableDictionary<string, StandingLocomotionPhaseEvent>.Builder phaseEvents =
            ImmutableDictionary.CreateBuilder<string, StandingLocomotionPhaseEvent>(StringComparer.Ordinal);
        if (leftRelease.HasValue)
        {
            events.Add("release_l", leftRelease.Value);
            phaseEvents.Add("release_l", new(
                "release_l",
                leftRelease.Value,
                StandingLocomotionEventKind.Release,
                StandingLocomotionAnatomicalSide.Left,
                StandingLocomotionBoundaryContext.Cyclic));
        }

        if (rightRelease.HasValue)
        {
            events.Add("release_r", rightRelease.Value);
            phaseEvents.Add("release_r", new(
                "release_r",
                rightRelease.Value,
                StandingLocomotionEventKind.Release,
                StandingLocomotionAnatomicalSide.Right,
                StandingLocomotionBoundaryContext.Cyclic));
        }

        return new(
            "ember_stride",
            WalkRole,
            StandingLocomotionBoundaryKind.CyclicTravel,
            IsLooping: true,
            duration,
            events.ToImmutable(),
            [],
            new Vector3(0f, 0f, -0.6f),
            0f,
            [])
        {
            Capability = StandingLocomotionCapability.CyclicForwardPrimary,
            PhaseEvents = phaseEvents.ToImmutable(),
        };
    }

    public static PlayerTravelReleaseCandidateV1 Candidate(
        PlayerTravelReleaseSide side = PlayerTravelReleaseSide.Left,
        string role = WalkRole,
        string exitRole = LeftExitRole,
        double eventPhase = 0d)
    {
        string suffix = side == PlayerTravelReleaseSide.Left ? "l" : "r";
        return new(
            SourceIdentity,
            role,
            $"ember/{role}.res",
            $"locomotion/ember_{role}",
            PlayerTravelEvaluatedSourceAuthority.SingularBlendPoint,
            $"release_{suffix}",
            side,
            exitRole,
            $"ember/{exitRole}.res",
            CatalogueProvenance,
            PlayerTravelReleaseClockTransformV1.Create(ReleaseTimeSeconds, CycleSeconds, CycleSeconds, ReleaseTimeSeconds - eventPhase))
        {
            EvaluatedCapability = role == ReverseRole
                ? StandingLocomotionCapability.CyclicBackwardPrimary
                : StandingLocomotionCapability.CyclicForwardPrimary,
            ExitCapability = StandingLocomotionCapabilities.Exit(
                role == ReverseRole,
                side == PlayerTravelReleaseSide.Left
                    ? StandingLocomotionAnatomicalSide.Left
                    : StandingLocomotionAnatomicalSide.Right),
        };
    }

    public static PlayerTravelClockEvaluationV1 Evaluation(
        long tick,
        double previous,
        double current,
        PlayerTravelReleaseSide side = PlayerTravelReleaseSide.Left,
        string role = WalkRole)
        => new(
            SourceIdentity,
            role,
            $"ember/{role}.res",
            $"locomotion/ember_{role}",
            PlayerTravelEvaluatedSourceAuthority.SingularBlendPoint,
            CatalogueProvenance,
            side,
            tick,
            previous,
            current,
            CycleSeconds,
            0.2d,
            PlayerTravelReleaseEvidenceOrigin.LiveEvaluatedClock,
            PlayerTravelReleaseFreshness.CurrentPhysicsTick);
}
