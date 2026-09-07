using Godot;

#pragma warning disable CS1591

namespace AlleyCat.IntegrationTests.Support;

/// <summary>One fixture-supplied physics-tick observation for navigation fluidity analysis.</summary>
public sealed record NPCFluiditySample(
    float TimeSeconds,
    string GraphNode,
    string ProgrammeStage,
    float NormalisedPhase,
    Vector2 BlendPoint,
    float PlaybackRate,
    Vector3 LeftFootWorld,
    Vector3 RightFootWorld,
    Vector3 RootWorld,
    FluidityContactFrame? Contacts = null,
    Vector3 ActorWorldPosition = default,
    Vector3 RealisedActorStepWorld = default,
    float RealisedRouteLongitudinal = 0f,
    float RouteProgress = 0f,
    float RemainingRouteDistance = 0f,
    Vector3 RouteTangentWorld = default,
    string PhaseLane = "",
    string FadingFromNode = "",
    bool TerminalApproachActive = false,
    int EntryCount = 0,
    int ExitCount = 0,
    Vector3 AuthoredRootWorldStep = default,
    Vector3 RequestedSyntheticWorldStep = default,
    bool MovementCollided = false);

/// <summary>One animation-graph node segment supplied by a navigation fixture.</summary>
public sealed record NPCFluidityNodeSegment(string NodeName, float EnterTimeSeconds, float ExitTimeSeconds);

/// <summary>Fixture-supplied samples and graph timeline for one named navigation case.</summary>
public sealed record NPCFluidityBoutRecord(
    string CaseName,
    float TickSeconds,
    string Detail,
    IReadOnlyList<NPCFluiditySample> Samples,
    IReadOnlyList<NPCFluidityNodeSegment> NodeTimeline);
