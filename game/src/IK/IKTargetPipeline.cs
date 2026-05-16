using Godot;

namespace AlleyCat.IK;

/// <summary>
/// Receives source and current target intent and returns the next requested IK target.
/// </summary>
public interface IIKTargetContributor
{
    /// <summary>
    /// Applies this contribution to the current target request.
    /// </summary>
    /// <param name="context">Source intent and current contributor-chain output.</param>
    /// <returns>The next requested target transform.</returns>
    Transform3D Contribute(IKTargetContributionContext context);
}

/// <summary>
/// Input supplied to a target contributor.
/// </summary>
/// <param name="SourceTarget">Authoritative source intent before contributors.</param>
/// <param name="CurrentTarget">Current requested target after earlier contributors.</param>
public readonly record struct IKTargetContributionContext(Transform3D SourceTarget, Transform3D CurrentTarget);

/// <summary>
/// Contributor that leaves the requested IK target unchanged.
/// </summary>
public sealed class NoOpIKTargetContributor : IIKTargetContributor
{
    /// <inheritdoc />
    public Transform3D Contribute(IKTargetContributionContext context) => context.CurrentTarget;
}

/// <summary>
/// Feedback describing the difference between requested and physically realised target transforms.
/// </summary>
/// <param name="Reason">Short reason for the feedback state.</param>
/// <param name="RequestedToRealisedDelta">World-space displacement from requested to realised target.</param>
/// <param name="ErrorDistance">Magnitude of <paramref name="RequestedToRealisedDelta" />.</param>
public readonly record struct IKTargetPipelineFeedback(
    string Reason,
    Vector3 RequestedToRealisedDelta,
    float ErrorDistance)
{
    /// <summary>
    /// Feedback for an exactly realised request.
    /// </summary>
    public static IKTargetPipelineFeedback None { get; } = new("None", Vector3.Zero, 0.0f);

    /// <summary>
    /// Creates feedback from requested and realised targets.
    /// </summary>
    public static IKTargetPipelineFeedback FromTargets(
        Transform3D requestedTarget,
        Transform3D realisedTarget,
        string reason)
    {
        Vector3 delta = realisedTarget.Origin - requestedTarget.Origin;
        return new IKTargetPipelineFeedback(reason, delta, delta.Length());
    }
}

/// <summary>
/// Requested target intent after source intent has passed through target contributors.
/// </summary>
/// <param name="SourceFollowState">Original source-intent follow state.</param>
/// <param name="RequestedFollowState">Contributor-adjusted follow state requested from physical actuation.</param>
public readonly record struct IKTargetPipelineRequest(
    IKTargetFollowState SourceFollowState,
    IKTargetFollowState RequestedFollowState)
{
    /// <summary>
    /// Creates a debug result before physical actuation has run.
    /// </summary>
    public IKTargetPipelineResult ToPendingResult()
        => new(
            SourceFollowState.WorldTransform,
            RequestedFollowState.WorldTransform,
            RequestedFollowState.WorldTransform,
            RequestedFollowState.Active ? IKTargetPipelineFeedback.None : IKTargetPipelineFeedback.FromTargets(
                RequestedFollowState.WorldTransform,
                RequestedFollowState.WorldTransform,
                "Inactive"));
}

/// <summary>
/// Debug snapshot for the IK target pipeline stages.
/// </summary>
/// <param name="SourceTarget">Authoritative source-intent target.</param>
/// <param name="RequestedTarget">Final constrained request after contributors.</param>
/// <param name="RealisedTarget">Final target after physical actuation.</param>
/// <param name="Feedback">Feedback from requested versus realised target.</param>
public readonly record struct IKTargetPipelineResult(
    Transform3D SourceTarget,
    Transform3D RequestedTarget,
    Transform3D RealisedTarget,
    IKTargetPipelineFeedback Feedback);

/// <summary>
/// Physical stage that actuates a constrained IK target pipeline request for the current frame.
/// </summary>
public interface IIKTargetActuator
{
    /// <summary>
    /// Applies a constrained request to the physical target representation.
    /// </summary>
    /// <param name="request">Current frame request emitted by the contributor stage.</param>
    /// <param name="delta">Frame delta in seconds.</param>
    /// <returns>Physical actuation output and feedback.</returns>
    IKTargetActuationResult Actuate(IKTargetPipelineRequest request, double delta);
}

/// <summary>
/// Result returned by a physical IK target actuation layer.
/// </summary>
/// <param name="RequestedTarget">Target requested from physical actuation.</param>
/// <param name="RealisedTarget">Target produced by physical actuation.</param>
/// <param name="Feedback">Feedback generated while actuating the target.</param>
public readonly record struct IKTargetActuationResult(
    Transform3D RequestedTarget,
    Transform3D RealisedTarget,
    IKTargetPipelineFeedback Feedback)
{
    /// <summary>
    /// Creates an inactive actuation result around the current physical target.
    /// </summary>
    public static IKTargetActuationResult Inactive(Transform3D target, string reason)
        => new(target, target, IKTargetPipelineFeedback.FromTargets(target, target, reason));
}

/// <summary>
/// Minimal source intent → contributors → request → actuation debug pipeline for one IK target path.
/// </summary>
public sealed class IKTargetPipeline(
    Func<IKTargetFollowState> sourceFollowStateProvider,
    Func<IReadOnlyList<IIKTargetContributor>?> contributorsProvider,
    IIKTargetActuator actuator)
{
    private readonly Func<IKTargetFollowState> _sourceFollowStateProvider = sourceFollowStateProvider
                                                                          ?? throw new ArgumentNullException(
                                                                              nameof(sourceFollowStateProvider));
    private readonly Func<IReadOnlyList<IIKTargetContributor>?> _contributorsProvider = contributorsProvider
                                                                                        ?? throw new ArgumentNullException(
                                                                                            nameof(contributorsProvider));
    private readonly IIKTargetActuator _actuator = actuator ?? throw new ArgumentNullException(nameof(actuator));

    /// <summary>
    /// Creates a pipeline with a fixed contributor list.
    /// </summary>
    public IKTargetPipeline(
        Func<IKTargetFollowState> sourceFollowStateProvider,
        IReadOnlyList<IIKTargetContributor>? contributors,
        IIKTargetActuator actuator)
        : this(sourceFollowStateProvider, () => contributors, actuator)
    {
    }

    /// <summary>
    /// Builds the current constrained request by applying each contributor to source intent in order.
    /// </summary>
    public IKTargetPipelineRequest PreviewRequest()
        => BuildRequest(_sourceFollowStateProvider(), _contributorsProvider());

    /// <summary>
    /// Runs source sampling, contribution, physical actuation, and feedback capture for one frame.
    /// </summary>
    public IKTargetPipelineResult Run(double delta)
    {
        IKTargetPipelineRequest request = PreviewRequest();
        IKTargetActuationResult actuation = _actuator.Actuate(request, delta);
        return new IKTargetPipelineResult(
            request.SourceFollowState.WorldTransform,
            request.RequestedFollowState.WorldTransform,
            actuation.RealisedTarget,
            actuation.Feedback);
    }

    private static IKTargetPipelineRequest BuildRequest(
        IKTargetFollowState sourceFollowState,
        IReadOnlyList<IIKTargetContributor>? contributors)
    {
        Transform3D requestedTarget = sourceFollowState.WorldTransform;
        if (sourceFollowState.Active && contributors is not null)
        {
            for (int index = 0; index < contributors.Count; index += 1)
            {
                IIKTargetContributor contributor = contributors[index];
                requestedTarget = contributor.Contribute(
                    new IKTargetContributionContext(sourceFollowState.WorldTransform, requestedTarget));
            }
        }

        return new IKTargetPipelineRequest(
            sourceFollowState,
            new IKTargetFollowState(requestedTarget, sourceFollowState.Active));
    }
}
