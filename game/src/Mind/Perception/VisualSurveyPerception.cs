using AlleyCat.Mind.Observation;
using AlleyCat.Vision;
using Godot;

namespace AlleyCat.Mind.Perception;

/// <summary>Emits one transient presence observation for every visibly surveyed subject identity.</summary>
[GlobalClass]
public sealed partial class VisualSurveyPerception : Perception<VisualSurveyPercept>
{
    /// <inheritdoc/>
    public override ValueTask PerceiveAsync(
        VisualSurveyPercept percept,
        PerceptionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(percept);
        ArgumentNullException.ThrowIfNull(context);
        foreach (string subjectFullId in percept.SubjectFullIDs)
        {
            Emit(new ObservedVisualPresence(subjectFullId));
        }

        return ValueTask.CompletedTask;
    }
}
