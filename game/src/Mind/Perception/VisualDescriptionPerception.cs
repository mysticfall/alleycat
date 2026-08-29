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
    protected override async ValueTask OnActiveLookChangedAsync(
        VisualCue? previousCue,
        IVisualSubject? previousSubject,
        VisualCue? currentCue,
        IVisualSubject? currentSubject,
        PerceptionContext context,
        CancellationToken cancellationToken)
    {
        if (currentCue is null
            || currentSubject is null
            || !IsLiveCue(currentCue))
        {
            return;
        }

        IdentityValidator.Validate(currentSubject, nameof(currentSubject));
        string subjectId = currentSubject.FullId;
        cancellationToken.ThrowIfCancellationRequested();
        string description = await currentCue.Describe(context.Scene, context.Character);
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsLiveCue(currentCue)
            || FindNearestSubject(currentCue) is not IVisualSubject revalidatedSubject
            || !ReferenceEquals(currentSubject, revalidatedSubject)
            || !string.Equals(subjectId, revalidatedSubject.FullId, StringComparison.Ordinal))
        {
            return;
        }

        IdentityValidator.Validate(revalidatedSubject, nameof(revalidatedSubject));
        Emit(new ObservedVisualDescription(subjectId, description));
    }
}
