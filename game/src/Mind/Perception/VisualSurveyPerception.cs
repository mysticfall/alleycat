using AlleyCat.Sense;
using Godot;

namespace AlleyCat.Mind.Perception;

/// <summary>Reinforces all visibly surveyed identities without creating observations.</summary>
[GlobalClass]
public sealed partial class VisualSurveyPerception : Perception<VisualSurveyPercept>
{
    private const float Contribution = 0.25f;

    /// <inheritdoc/>
    public override PerceptionResult Perceive(VisualSurveyPercept percept, PerceptionContext context)
    {
        ArgumentNullException.ThrowIfNull(percept);
        ArgumentNullException.ThrowIfNull(context);
        var effects = new AttentionEffect[percept.SubjectFullIDs.Count];
        for (int index = 0; index < effects.Length; index++)
        {
            effects[index] = new AttentionEffect(percept.SubjectFullIDs[index], Contribution);
        }

        return new PerceptionResult(effects, []);
    }
}
