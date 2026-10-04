using System.Collections.Immutable;
using AlleyCat.Control.Locomotion;
using Godot;

namespace AlleyCat.Tests.Control;

/// <summary>Test-owned declaration for the player travel seam-alignment contract.</summary>
internal static class SyntheticTravelFixture
{
    public const string WalkRole = "wf";
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
}
