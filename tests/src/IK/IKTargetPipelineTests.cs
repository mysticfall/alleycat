using AlleyCat.IK;
using Godot;
using Xunit;

namespace AlleyCat.Tests.IK;

/// <summary>
/// Unit coverage for the foundational IK target pipeline.
/// </summary>
public sealed class IKTargetPipelineTests
{
    /// <inheritdoc/>
    [Fact]
    public void PreviewRequest_WithNoContributors_MatchesSourceTarget()
    {
        IKTargetFollowState source = new(CreateTransform(new Vector3(1.0f, 2.0f, 3.0f)), active: true);
        IKTargetPipeline pipeline = new(() => source, contributors: null, new IdentityActuator());

        IKTargetPipelineRequest request = pipeline.PreviewRequest();

        AssertTransformEqual(source.WorldTransform, request.SourceFollowState.WorldTransform);
        AssertTransformEqual(source.WorldTransform, request.RequestedFollowState.WorldTransform);
        Assert.True(request.RequestedFollowState.Active);
    }

    /// <inheritdoc/>
    [Fact]
    public void NoOpContributor_ReturnsCurrentTargetUnchanged()
    {
        Transform3D sourceTarget = CreateTransform(new Vector3(5.0f, 0.0f, 0.0f));
        Transform3D currentTarget = CreateTransform(new Vector3(0.25f, 0.5f, 0.75f));
        NoOpIKTargetContributor contributor = new();

        Transform3D result = contributor.Contribute(new IKTargetContributionContext(sourceTarget, currentTarget));

        AssertTransformEqual(currentTarget, result);
    }

    /// <inheritdoc/>
    [Fact]
    public void Run_WithNoOpContributor_MatchesCurrentActuatorRequest()
    {
        IKTargetFollowState source = new(CreateTransform(new Vector3(-1.0f, 0.25f, 2.0f)), active: true);
        IIKTargetContributor[] contributors = [new NoOpIKTargetContributor()];
        IKTargetPipeline pipeline = new(() => source, contributors, new IdentityActuator());

        IKTargetPipelineResult result = pipeline.Run(delta: 1.0d / 60.0d);

        AssertTransformEqual(source.WorldTransform, result.SourceTarget);
        AssertTransformEqual(source.WorldTransform, result.RequestedTarget);
        AssertTransformEqual(source.WorldTransform, result.RealisedTarget);
        Assert.Equal("None", result.Feedback.Reason);
        Assert.True(result.Feedback.RequestedToRealisedDelta.IsEqualApprox(Vector3.Zero));
    }

    /// <inheritdoc/>
    [Fact]
    public void Run_IncludesDebugSourceRequestedRealisedAndFeedbackFields()
    {
        IKTargetFollowState source = new(CreateTransform(new Vector3(1.0f, 0.0f, 0.0f)), active: true);
        Transform3D realised = CreateTransform(new Vector3(1.0f, 0.5f, 0.0f));
        FixedActuator actuator = new(realised, "Collision");
        IKTargetPipeline pipeline = new(
            () => source,
            [new OffsetContributor(new Vector3(0.0f, 1.0f, 0.0f))],
            actuator);

        IKTargetPipelineResult result = pipeline.Run(delta: 1.0d / 60.0d);

        Assert.Equal(1, actuator.CallCount);
        Assert.Equal(1.0d / 60.0d, actuator.LastDelta);
        AssertTransformEqual(CreateTransform(new Vector3(1.0f, 1.0f, 0.0f)), actuator.LastRequest.RequestedFollowState.WorldTransform);
        AssertTransformEqual(source.WorldTransform, result.SourceTarget);
        AssertTransformEqual(CreateTransform(new Vector3(1.0f, 1.0f, 0.0f)), result.RequestedTarget);
        AssertTransformEqual(realised, result.RealisedTarget);
        Assert.Equal("Collision", result.Feedback.Reason);
        Assert.True(result.Feedback.RequestedToRealisedDelta.IsEqualApprox(new Vector3(0.0f, -0.5f, 0.0f)));
        Assert.InRange(result.Feedback.ErrorDistance, 0.499f, 0.501f);
    }

    private static Transform3D CreateTransform(Vector3 origin) => new(Basis.Identity, origin);

    private static void AssertTransformEqual(Transform3D expected, Transform3D actual)
    {
        Assert.True(actual.Origin.IsEqualApprox(expected.Origin), $"Expected origin {expected.Origin}, got {actual.Origin}.");
        Assert.True(actual.Basis.IsEqualApprox(expected.Basis), $"Expected basis {expected.Basis}, got {actual.Basis}.");
    }

    private sealed class OffsetContributor(Vector3 offset) : IIKTargetContributor
    {
        public Transform3D Contribute(IKTargetContributionContext context)
            => new(context.CurrentTarget.Basis, context.CurrentTarget.Origin + offset);
    }

    private sealed class IdentityActuator : IIKTargetActuator
    {
        public IKTargetActuationResult Actuate(IKTargetPipelineRequest request, double delta)
            => new(
                request.RequestedFollowState.WorldTransform,
                request.RequestedFollowState.WorldTransform,
                IKTargetPipelineFeedback.None);
    }

    private sealed class FixedActuator(Transform3D realisedTarget, string reason) : IIKTargetActuator
    {
        public int CallCount
        {
            get; private set;
        }

        public double LastDelta
        {
            get; private set;
        }

        public IKTargetPipelineRequest LastRequest
        {
            get; private set;
        }

        public IKTargetActuationResult Actuate(IKTargetPipelineRequest request, double delta)
        {
            CallCount++;
            LastDelta = delta;
            LastRequest = request;
            return new IKTargetActuationResult(
                request.RequestedFollowState.WorldTransform,
                realisedTarget,
                IKTargetPipelineFeedback.FromTargets(request.RequestedFollowState.WorldTransform, realisedTarget, reason));
        }
    }
}
