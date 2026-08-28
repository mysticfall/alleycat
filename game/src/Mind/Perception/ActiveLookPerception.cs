using AlleyCat.Vision;
namespace AlleyCat.Mind.Perception;

/// <summary>Reusable, independently owned perception base tracking one active visual cue.</summary>
public abstract partial class ActiveLookPerception : Perception<LookTargetChangedPercept>
{
    /// <summary>Gets the cue most recently supplied by a target transition.</summary>
    internal VisualCue? ActiveCue
    {
        get; private set;
    }

    /// <inheritdoc />
    public sealed override ValueTask<PerceptionResult> PerceiveAsync(
        LookTargetChangedPercept percept,
        PerceptionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(percept);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        ActiveCue = percept.Current;
        return percept.Current is null
            ? ValueTask.FromResult(new PerceptionResult([], []))
            : PerceiveActiveCueAsync(percept.Current, context, cancellationToken);
    }

    /// <summary>Interprets a newly active non-null cue.</summary>
    protected abstract ValueTask<PerceptionResult> PerceiveActiveCueAsync(
        VisualCue cue,
        PerceptionContext context,
        CancellationToken cancellationToken);
}
