using AlleyCat.Core;
using AlleyCat.Mind.Observation;
using AlleyCat.Vision;
using Godot;

namespace AlleyCat.Mind.Perception;

/// <summary>Interprets visual focus transitions into durable subject descriptions.</summary>
[GlobalClass]
public sealed partial class VisualDescriptionPerception : ActiveLookPerception
{
    /// <inheritdoc />
    protected override async ValueTask<PerceptionResult> PerceiveActiveCueAsync(
        VisualCue cue,
        PerceptionContext context,
        CancellationToken cancellationToken)
    {
        if (!IsLiveCue(cue) || FindNearestSubject(cue) is not IVisualSubject subject)
        {
            return new PerceptionResult([], []);
        }

        IdentityValidator.Validate(subject, nameof(subject));
        string subjectId = subject.FullId;
        cancellationToken.ThrowIfCancellationRequested();
        string description = await cue.Describe(context.Scene, context.Character);
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsLiveCue(cue)
            || FindNearestSubject(cue) is not IVisualSubject currentSubject
            || !ReferenceEquals(subject, currentSubject)
            || !string.Equals(subjectId, currentSubject.FullId, StringComparison.Ordinal))
        {
            return new PerceptionResult([], []);
        }

        IdentityValidator.Validate(currentSubject, nameof(currentSubject));
        return new PerceptionResult([], [new ObservedVisualDescription(subjectId, description)]);
    }

    private static bool IsLiveCue(VisualCue cue)
        => IsInstanceValid(cue) && cue.IsInsideTree() && !cue.IsQueuedForDeletion();

    private static IVisualSubject? FindNearestSubject(VisualCue cue)
    {
        for (Node? ancestor = cue.GetParent(); ancestor is not null; ancestor = ancestor.GetParent())
        {
            if (ancestor is IVisualSubject subject)
            {
                return subject;
            }
        }

        return null;
    }
}
