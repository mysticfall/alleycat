using AlleyCat.Animations;

namespace AlleyCat.Mind.AI.Tool;

/// <summary>Outcome of one model-requested animation preference change.</summary>
/// <param name="Accepted">Whether the character accepted the preference.</param>
/// <param name="Message">In-character result returned to the model.</param>
internal readonly record struct AnimationPreferenceOutcome(bool Accepted, string Message);

/// <summary>
/// Applies a model-supplied catalogue id as the character's standing agent preference for one category
/// (ANIM-003 Mind Integration, Agent Tools). The call returns as soon as the preference is accepted; it never waits for
/// a transition. Blank, unknown and incompatible ids are refused and leave the preference unchanged.
/// </summary>
internal static class AnimationPreferenceRequest
{
    /// <summary>Applies <paramref name="id" /> through <paramref name="control" /> for <paramref name="category" />.</summary>
    public static AnimationPreferenceOutcome Apply(
        IAnimationVariantControl control,
        AnimationCategory category,
        string? id)
    {
        ArgumentNullException.ThrowIfNull(control);
        // Tolerate a copied backticked id; the catalogue shows ids in backticks.
        string normalised = (id ?? string.Empty).Trim().Trim('`').Trim();
        if (normalised.Length == 0)
        {
            // A blank id would clear the preference; the tool only ever adopts a listed entry.
            return new AnimationPreferenceOutcome(false, BlankRefusal(category));
        }

        bool accepted = category switch
        {
            AnimationCategory.Idle => control.SetIdlePreference(normalised),
            AnimationCategory.Locomotion => control.SetLocomotionPreference(normalised),
            _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unsupported animation category."),
        };

        return accepted
            ? new AnimationPreferenceOutcome(true, Success(category, normalised))
            : new AnimationPreferenceOutcome(false, Refusal(category, normalised));
    }

    private static string Success(AnimationCategory category, string id) => category == AnimationCategory.Idle
        ? $"You will take up the stance `{id}` whenever you stand still."
        : $"You will move with the gait `{id}` from your current or next movement.";

    private static string BlankRefusal(AnimationCategory category) => category == AnimationCategory.Idle
        ? "You cannot take up a stance without naming one; your stance is unchanged."
        : "You cannot change your gait without naming one; your gait is unchanged.";

    private static string Refusal(AnimationCategory category, string id) => category == AnimationCategory.Idle
        ? $"You cannot take up the stance `{id}`; your stance is unchanged. Choose one of the stances listed for you."
        : $"You cannot move with the gait `{id}`; your gait is unchanged. Choose one of the gaits listed for you.";
}
