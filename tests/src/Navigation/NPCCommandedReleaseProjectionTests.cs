using AlleyCat.Navigation;
using Godot;
using Xunit;

#pragma warning disable CS1591

namespace AlleyCat.Tests.Navigation;

public sealed class NPCCommandedReleaseProjectionTests
{
    private const float Delta = 1f / 60f;
    private static readonly Vector2 _pin = new(1f, 0f);

    [Fact]
    public void RequestDefault_PreservesSustainedRateAndCommitsOnConsumedForwardInterval()
    {
        NPCCommandedReleaseProjectionRequest request = CreateRequest(_pin, 0.99f, 0f) with
        {
            RequireBlendConvergenceBeforeRelease = false,
        };

        NPCCommandedReleaseProjectionResult result = Project(request, ForwardInterval(0.5f));

        Assert.True(result.Resolved);
        Assert.Equal(1, result.ProjectedTicks);
        Assert.Equal(0.99f, result.PhaseAtRelease, 5);
        Assert.False(NPCExitReleaseGate.IsAtRelease(result.PhaseAtRelease, 0f));
        Assert.Equal(request.OrdinaryPlaybackRate, result.PlaybackRateAtRelease, 5);
    }

    [Theory]
    [InlineData(0.99f, 0f)]
    [InlineData(0.49f, 0.5f)]
    public void PreserveRate_FirstConsumedForwardIntervalCommitsWithoutExactSample(float phase, float target)
    {
        NPCCommandedReleaseProjectionRequest request = CreateRequest(_pin, phase, target) with
        {
            RequireBlendConvergenceBeforeRelease = false,
            OrdinaryPlaybackRate = 1f,
        };

        NPCCommandedReleaseProjectionResult result = Project(request, ForwardInterval(0.5f));

        Assert.True(result.Resolved);
        Assert.Equal(1, result.ProjectedTicks);
        Assert.Equal(phase, result.PhaseAtRelease, 5);
        Assert.False(NPCExitReleaseGate.IsAtRelease(result.PhaseAtRelease, target));
        Assert.True(NPCExitReleaseGate.ReachesReleaseThisFrame(
            new(true, true, phase, phase, 1f, 1f, Delta),
            target));
        Assert.Equal(1f, result.PlaybackRateAtRelease);
    }

    [Fact]
    public void PreserveRate_DoesNotCommitBeforeReleaseEntersConsumedInterval()
    {
        NPCCommandedReleaseProjectionRequest request = CreateRequest(_pin, 0.9f, 0f) with
        {
            RequireBlendConvergenceBeforeRelease = false,
            OrdinaryPlaybackRate = 1f,
        };

        NPCCommandedReleaseProjectionResult result = Project(request, ForwardInterval(0.5f), maximumTicks: 1);

        Assert.False(result.Resolved);
    }

    [Fact]
    public void PreserveRate_LargeFrameCommitsOnceThroughSharedCrossingGate()
    {
        NPCCommandedReleaseProjectionRequest request = CreateRequest(_pin, 0.1f, 0.8f) with
        {
            DeltaSeconds = 1.1f,
            RequireBlendConvergenceBeforeRelease = false,
            OrdinaryPlaybackRate = 1f,
        };

        NPCCommandedReleaseProjectionResult result = Project(request, ForwardInterval(0.5f));

        Assert.True(result.Resolved);
        Assert.Equal(1, result.ProjectedTicks);
        Assert.Equal(0.1f, result.PhaseAtRelease, 5);
    }

    [Fact]
    public void PreserveRate_SelectedLaterOccurrencePassesEarlierSamePhaseAtOrdinaryRate()
    {
        NPCReleaseOccurrence occurrence = NPCExitReleaseGate.SelectOccurrence(
            7,
            0.9d,
            0f,
            NPCAnimationPhaseLane.Left,
            "release_l",
            additionalCycles: 1);
        NPCCommandedReleaseProjectionRequest request = CreateRequest(_pin, 0.9f, 0f) with
        {
            RequireBlendConvergenceBeforeRelease = false,
            OrdinaryPlaybackRate = 1f,
            CurrentUnwrappedPhase = 0.9d,
            SelectedOccurrence = occurrence,
        };

        NPCCommandedReleaseProjectionResult result = Project(request, ForwardInterval(0.5f));

        Assert.True(result.Resolved);
        Assert.Equal(occurrence, result.SelectedOccurrence);
        Assert.Equal(2d, occurrence.UnwrappedPhase, 6);
        Assert.InRange(result.TimeToReleaseSeconds, 1.09f, 1.12f);
        Assert.Equal(1f, result.PlaybackRateAtRelease);
        Assert.InRange(result.SustainedRootUntilReleaseWorld.Length(), 0.54f, 0.57f);
    }

    [Fact]
    public void PreserveRate_MissedSelectedOccurrenceDoesNotCommitLaterSamePhase()
    {
        NPCReleaseOccurrence occurrence = NPCExitReleaseGate.SelectOccurrence(
            7,
            0.4d,
            0.5f,
            NPCAnimationPhaseLane.Right,
            "release_r");
        NPCCommandedReleaseProjectionRequest request = CreateRequest(_pin, 0.6f, 0.5f) with
        {
            RequireBlendConvergenceBeforeRelease = false,
            CurrentUnwrappedPhase = 0.6d,
            SelectedOccurrence = occurrence,
        };

        NPCCommandedReleaseProjectionResult result = Project(request, ForwardInterval(0.5f));

        Assert.False(result.Resolved);
        Assert.Equal(occurrence, result.SelectedOccurrence);
    }

    [Fact]
    public void BoundedHorizon_NeverExceedsCallerTicksAndStaysUnresolvedPastTheBound()
    {
        // The occurrence is a full cycle ahead; a one-tick bound cannot reach it and must stay unresolved.
        NPCCommandedReleaseProjectionRequest request = CreateRequest(_pin, 0.1f, 0.9f) with
        {
            RequireBlendConvergenceBeforeRelease = false,
            OrdinaryPlaybackRate = 1f,
            CurrentUnwrappedPhase = 0.1d,
        };

        NPCCommandedReleaseProjectionResult result = Project(request, ForwardInterval(0.5f), maximumTicks: 1);

        Assert.False(result.Resolved);
        Assert.Equal(1, result.ProjectedTicks);
    }

    [Fact]
    public void Convergence_CompletesBeforeReleaseAndArrivesExactlyAtPin()
    {
        NPCCommandedReleaseProjectionResult result = Project(
            CreateRequest(new Vector2(0.06f, 0.32f), 0.1f, 0.6f),
            ForwardInterval(0.5f));

        Assert.True(result.Resolved);
        Assert.True(result.BlendConverged);
        Assert.InRange(
            result.ConvergenceSeconds,
            Delta,
            NPCCommandedReleaseProjection.MaximumConvergenceSeconds);
        Assert.True(result.ConvergenceSeconds <= result.TimeToReleaseSeconds);
        Assert.True(result.BlendPointAtRelease.DistanceTo(_pin) <= 0.0001f);
        Assert.Equal(result.ProjectedTicks * Delta, result.TimeToReleaseSeconds, 5);
    }

    [Fact]
    public void StepPlaybackRate_MovesTowardTargetUnderAccelerationAndJerkLimits()
    {
        // The publisher's landing engagement: a 1.0 -> 0.6685 plan takes the first smoothed step of only
        // -0.00278 (jerk-limited from rest), reaching the target exactly clears the acceleration.
        float acceleration = 0f;
        Assert.Equal(0.9972222f, NPCCommandedReleaseProjection.StepPlaybackRate(1f, 0.6685192f, ref acceleration, Delta), 6);
        Assert.True(acceleration < 0f);
        float settling = NPCCommandedReleaseProjection.StepPlaybackRate(1f, 1f, ref acceleration, Delta);
        Assert.Equal(1f, settling, 6);

        acceleration = 0f;
        float reached = NPCCommandedReleaseProjection.StepPlaybackRate(0.9f, 0.9000001f, ref acceleration, Delta);
        Assert.Equal(0.9000001f, reached, 7);
        Assert.Equal(0f, acceleration, 6);
    }

    [Fact]
    public void YawIntegration_RotatesDisplacementIntoTheProgressivelyRotatedFrame()
    {
        NPCCommandedReleaseProjectionResult zeroYaw = Project(
            CreateRequest(_pin, 0.1f, 0.6f, actorYaw: 0f, exitStartYaw: 0f),
            ForwardInterval(1f));
        NPCCommandedReleaseProjectionResult quarterYaw = Project(
            CreateRequest(_pin, 0.1f, 0.6f, actorYaw: Mathf.Pi / 2f, exitStartYaw: Mathf.Pi / 2f),
            ForwardInterval(1f));

        Assert.True(zeroYaw.Resolved);
        Assert.True(quarterYaw.Resolved);
        Assert.Equal(zeroYaw.ProjectedTicks, quarterYaw.ProjectedTicks);
        Assert.Equal(zeroYaw.SustainedRootUntilReleaseWorld.Length(), quarterYaw.SustainedRootUntilReleaseWorld.Length(), 4);
        Vector3 rotated = new Basis(Vector3.Up, Mathf.Pi / 2f)
            * new Vector3(zeroYaw.SustainedRootUntilReleaseWorld.X, 0f, zeroYaw.SustainedRootUntilReleaseWorld.Y);
        Assert.Equal(rotated.X, quarterYaw.SustainedRootUntilReleaseWorld.X, 4);
        Assert.Equal(rotated.Z, quarterYaw.SustainedRootUntilReleaseWorld.Y, 4);
    }

    [Theory]
    [InlineData(1.2f, 0.2f, 0.5f, 1f)]
    [InlineData(-1.2f, -0.2f, -0.5f, -1f)]
    public void ExitYawResidual_UsesResolvedSelectedOccurrenceSymmetrically(
        float terminalYaw,
        float exitEndpointYaw,
        float armYaw,
        float projectedYaw)
    {
        var resolved = new NPCCommandedReleaseProjectionResult(
            BlendConverged: true,
            ConvergenceSeconds: 0.2f,
            Resolved: true,
            TimeToReleaseSeconds: 0.5f,
            TargetPhase: 0.5f,
            SustainedRootUntilReleaseWorld: Vector2.Zero,
            BlendPointAtRelease: _pin,
            PhaseAtRelease: 0.5f,
            PlaybackRateAtRelease: 1f,
            YawAtRelease: projectedYaw,
            ProjectedTicks: 30);

        float projectedResidual = NPCCommandedReleaseProjection.ResolveExitYawResidual(
            terminalYaw,
            exitEndpointYaw,
            armYaw,
            resolved);
        float unresolvedResidual = NPCCommandedReleaseProjection.ResolveExitYawResidual(
            terminalYaw,
            exitEndpointYaw,
            armYaw,
            NPCCommandedReleaseProjectionResult.Unresolved);

        Assert.Equal(0f, projectedResidual, 5);
        Assert.True(unresolvedResidual > NPCTerminalReleaseScheduler.RootRotationTolerance);
    }

    [Fact]
    public void Deterministic_SameInputsProduceIdenticalResults()
    {
        NPCCommandedReleaseProjectionRequest request = CreateRequest(
            new Vector2(0.06f, 0.32f),
            0.1f,
            0.6f);

        NPCCommandedReleaseProjectionResult first = Project(request, ForwardInterval(0.5f));
        NPCCommandedReleaseProjectionResult second = Project(request, ForwardInterval(0.5f));

        Assert.Equal(first, second);
    }

    [Fact]
    public void Capture_ReportsEveryExactIntervalAndReconcilesAuthoredRootAndYaw()
    {
        NPCCommandedReleaseProjectionRequest request = CreateRequest(_pin, 0.1f, 0.6f);
        List<NPCCommandedReleaseProjectionInterval> capture = [];

        NPCCommandedReleaseProjectionResult result = NPCCommandedReleaseProjection.Project(
            request,
            ForwardInterval(0.5f),
            capture,
            maximumTicks: 240);

        Assert.True(result.Resolved);
        Assert.Equal(result.ProjectedTicks, capture.Count);
        Vector2 cumulativeRoot = new(
            capture.Sum(interval => interval.AuthoredRootWorld.X),
            capture.Sum(interval => interval.AuthoredRootWorld.Y));
        Assert.Equal(result.SustainedRootUntilReleaseWorld.X, cumulativeRoot.X, 6);
        Assert.Equal(result.SustainedRootUntilReleaseWorld.Y, cumulativeRoot.Y, 6);
        Assert.Equal(
            NPCNavigationRoutePlan.WrapAngle(result.YawAtRelease - request.ActorYaw),
            NPCNavigationRoutePlan.WrapAngle(capture.Sum(interval => interval.AuthoredYawRadians)),
            4);
        for (int index = 0; index < capture.Count; index++)
        {
            Assert.Equal(index, capture[index].IntervalIndex);
            Assert.Equal(request.DeltaSeconds, capture[index].DeltaSeconds, 7);
        }

        // The capture is caller-owned reusable storage: a second projection clears it before refilling.
        _ = NPCCommandedReleaseProjection.Project(
            request,
            ForwardInterval(0.5f),
            capture,
            maximumTicks: 240);
        Assert.Equal(result.ProjectedTicks, capture.Count);
    }

    [Fact]
    public void WarmCapturePath_AllocatesNoManagedMemory()
    {
        NPCCommandedReleaseProjectionRequest request = CreateRequest(_pin, 0.1f, 0.6f);
        List<NPCCommandedReleaseProjectionInterval> capture = [];
        Func<Vector2, float, Vector2, float, float, NPCCommandedReleaseAuthoredIntervalSample> sampler =
            ForwardInterval(0.5f);
        _ = NPCCommandedReleaseProjection.Project(request, sampler, capture, maximumTicks: 240);
        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int index = 0; index < 100; index++)
        {
            _ = NPCCommandedReleaseProjection.Project(request, sampler, capture, maximumTicks: 240);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void ReleaseSeamGate_PassesAtPinBlendWithoutConsultingTheProjection()
    {
        NPCCommandedReleaseProjectionResult unresolved = NPCCommandedReleaseProjectionResult.Unresolved;

        Assert.True(NPCCommandedReleaseProjection.PassesExitSeamReleaseGate(
            unresolved,
            NPCCommandedReleaseProjection.ExitSeamAtPinRadius - 0.001f));
        Assert.False(NPCCommandedReleaseProjection.PassesExitSeamReleaseGate(
            unresolved,
            NPCCommandedReleaseProjection.ExitSeamAtPinRadius + 0.001f));
    }

    [Fact]
    public void ReleaseSeamGate_PassesOnlyWhenProjectedConvergenceCompletesByTheReleaseBeat()
    {
        var projection = new NPCCommandedReleaseProjectionResult(
            BlendConverged: true,
            ConvergenceSeconds: 0.2f,
            Resolved: true,
            TimeToReleaseSeconds: 0.5f,
            TargetPhase: 0.5f,
            SustainedRootUntilReleaseWorld: Vector2.Zero,
            BlendPointAtRelease: _pin,
            PhaseAtRelease: 0.5f,
            PlaybackRateAtRelease: 1f,
            YawAtRelease: 0f,
            ProjectedTicks: 30);
        NPCCommandedReleaseProjectionResult lateConvergence = projection with
        {
            ConvergenceSeconds = 0.6f,
        };
        NPCCommandedReleaseProjectionResult unresolved = projection with
        {
            Resolved = false,
        };

        Assert.True(NPCCommandedReleaseProjection.PassesExitSeamReleaseGate(projection, 1f));
        Assert.False(NPCCommandedReleaseProjection.PassesExitSeamReleaseGate(lateConvergence, 1f));
        Assert.False(NPCCommandedReleaseProjection.PassesExitSeamReleaseGate(unresolved, 1f));
    }

    private static NPCCommandedReleaseProjectionResult Project(
        NPCCommandedReleaseProjectionRequest request,
        Func<Vector2, float, Vector2, float, float, NPCCommandedReleaseAuthoredIntervalSample> sampler,
        int maximumTicks = 240)
        => NPCCommandedReleaseProjection.Project(request, sampler, maximumTicks);

    private static NPCCommandedReleaseProjectionRequest CreateRequest(
        Vector2 currentBlend,
        float phase,
        float targetPhase,
        float playbackRate = 1f,
        float actorYaw = 0f,
        float exitStartYaw = 0f,
        float cyclicLength = 1f) => new(
        currentBlend,
        Vector2.Zero,
        _pin,
        phase,
        playbackRate,
        cyclicLength,
        targetPhase,
        actorYaw,
        exitStartYaw,
        Delta);

    private static Func<Vector2, float, Vector2, float, float, NPCCommandedReleaseAuthoredIntervalSample>
        ForwardInterval(float forwardSpeed)
    {
        float delta = Delta;
        return (_, _, _, _, _) => new NPCCommandedReleaseAuthoredIntervalSample(
            new Vector2(0f, forwardSpeed * delta),
            0f);
    }
}
