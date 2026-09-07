using AlleyCat.IntegrationTests.Support;
using Godot;
using Xunit;

#pragma warning disable CS1591

namespace AlleyCat.IntegrationTests.Navigation;

/// <summary>Focused detector coverage for the reported terminal paired-foot-hop and terminal-resolution episode.</summary>
public sealed class NPCNavigationExitStruggleIntegrationTests
{
    [Fact]
    public void DetectorOnly_NormalAlternatingLargeSingleFootSteps_DoNotTriggerEpisode()
    {
        ExitStruggleResult result = ExitStruggleDetector.Evaluate(
            "detector-only-normal-alternating-single-foot-steps",
            [
                CreateSample(0, 0.8f, 0.90f, Vector3.Zero, Vector3.Zero),
                CreateSample(1, 0.8f, 0.82f, new Vector3(0.60f, 0f, 0f), Vector3.Zero),
                CreateSample(2, 0.8f, 0.74f, new Vector3(0.60f, 0f, 0f), new Vector3(0.60f, 0f, 0f)),
                CreateSample(3, 0.8f, 0.66f, new Vector3(1.20f, 0f, 0f), new Vector3(0.60f, 0f, 0f)),
            ],
            _detectorOnlyThresholds);

        Assert.Empty(result.PairedHops);
        Assert.False(result.EpisodeDetected, result.BuildReport());
    }

    [Fact]
    public void DetectorOnly_SimultaneousFootMotionAfterArrival_DoesNotTriggerEpisode()
    {
        ExitStruggleResult result = ExitStruggleDetector.Evaluate(
            "detector-only-simultaneous-foot-motion-after-arrival",
            [
                CreateSample(0, 0.8f, 0.90f, Vector3.Zero, Vector3.Zero, terminalApproach: false),
                CreateSample(1, 0.8f, 0.02f, Vector3.Zero, Vector3.Zero),
                CreateSample(2, 0f, 0.01f, new Vector3(0.80f, 0f, 0f), new Vector3(0.80f, 0f, 0f), stage: "Complete"),
                CreateSample(3, 0f, 0.01f, new Vector3(1.60f, 0f, 0f), new Vector3(1.60f, 0f, 0f), stage: "Complete"),
            ],
            _detectorOnlyThresholds);

        Assert.Empty(result.PairedHops);
        Assert.False(result.EpisodeDetected, result.BuildReport());
    }

    [Fact]
    public void DetectorOnly_NoPausePairedHopAndTerminalResolution_IsDetected()
    {
        ExitStruggleResult result = ExitStruggleDetector.Evaluate(
            "detector-only-no-pause-terminal-paired-hop",
            [
                CreateSample(0, 0.8f, 0.90f, Vector3.Zero, Vector3.Zero, terminalApproach: false),
                CreateSample(1, 0.8f, 0.80f, Vector3.Zero, Vector3.Zero),
                CreateSample(2, 0.8f, 0.70f, new Vector3(0.80f, 0f, 0f), new Vector3(0.80f, 0f, 0f)),
                CreateSample(3, 2.0f, 0.01f, new Vector3(1.60f, 0f, 0f), new Vector3(1.60f, 0f, 0f), stage: "Complete"),
            ],
            _detectorOnlyThresholds);

        Assert.Empty(result.Pauses);
        ExitStruggleEpisode episode = Assert.IsType<ExitStruggleEpisode>(result.Episode);
        Assert.Equal(2, episode.PairedHop.OnsetSampleIndex);
        Assert.Equal(3, episode.TerminalResolution.SampleIndex);
        Assert.True(episode.TerminalResolution.IsMaterialArrivalOrCompletion, result.BuildReport());
    }

    [Fact]
    public void DetectorOnly_NonterminalPairedMotion_DoesNotTriggerEpisode()
    {
        ExitStruggleResult result = ExitStruggleDetector.Evaluate(
            "detector-only-nonterminal-paired-motion",
            [
                CreateSample(0, 0.8f, 2.00f, Vector3.Zero, Vector3.Zero, terminalApproach: false),
                CreateSample(1, 0.8f, 1.90f, new Vector3(0.80f, 0f, 0f), new Vector3(0.80f, 0f, 0f), terminalApproach: false),
                CreateSample(2, 0.8f, 1.80f, new Vector3(1.60f, 0f, 0f), new Vector3(1.60f, 0f, 0f), terminalApproach: false),
            ],
            _detectorOnlyThresholds);

        Assert.Equal(result.Samples.Count, result.TerminalStartSampleIndex);
        Assert.Empty(result.PairedHops);
        Assert.False(result.EpisodeDetected, result.BuildReport());
    }

    private const float DetectorOnlyTickSeconds = 0.1f;

    private static readonly ExitStruggleThresholds _detectorOnlyThresholds = new(
        FallbackTerminalRemainingDistance: 1f,
        ArrivalRemainingDistance: 0.03f,
        MaximumPauseRouteSpeed: 0.05f,
        MinimumPauseTicks: 3,
        FootHopPlanarStep: 0.5f,
        MaterialActorPlanarStep: 0.5f,
        MaterialRemainingDistanceDrop: 0.5f,
        RouteSpeedDiscontinuity: 1.5f,
        RouteAccelerationDiscontinuity: 4f,
        MaximumTicksBetweenFeet: 1,
        MaximumTicksFromHopToTerminalResolution: 4);

    private static NPCFluiditySample CreateSample(
        int index,
        float routeSpeed,
        float remainingDistance,
        Vector3 leftFoot,
        Vector3 rightFoot,
        bool terminalApproach = true,
        string stage = "Exit") => new(
            TimeSeconds: index * DetectorOnlyTickSeconds,
                GraphNode: "NPCTravel",
            ProgrammeStage: stage,
            NormalisedPhase: 0f,
            BlendPoint: Vector2.Zero,
            PlaybackRate: 1f,
            LeftFootWorld: leftFoot,
            RightFootWorld: rightFoot,
            RootWorld: Vector3.Zero,
            ActorWorldPosition: Vector3.Zero,
            RealisedActorStepWorld: Vector3.Forward * routeSpeed * DetectorOnlyTickSeconds,
            RealisedRouteLongitudinal: routeSpeed * DetectorOnlyTickSeconds,
            RemainingRouteDistance: remainingDistance,
            RouteTangentWorld: Vector3.Forward,
            TerminalApproachActive: terminalApproach);

}
