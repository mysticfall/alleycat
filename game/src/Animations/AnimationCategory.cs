namespace AlleyCat.Animations;

/// <summary>
/// Variant animation categories covered by the ANIM-003 animation catalogue.
/// </summary>
public enum AnimationCategory
{
    /// <summary>A native idle clip played on the reference character skeleton.</summary>
    Idle,

    /// <summary>A locomotion variant backed by one ANIM-002 reviewed set.</summary>
    Locomotion
}

/// <summary>
/// Helpers for the authored names of <see cref="AnimationCategory" /> values.
/// </summary>
public static class AnimationCategoryNames
{
    /// <summary>Authored name of <see cref="AnimationCategory.Idle" />.</summary>
    public const string Idle = "idle";

    /// <summary>Authored name of <see cref="AnimationCategory.Locomotion" />.</summary>
    public const string Locomotion = "locomotion";

    /// <summary>Returns the authored, lowercase name of a category.</summary>
    public static string ToName(this AnimationCategory category) => category switch
    {
        AnimationCategory.Idle => Idle,
        AnimationCategory.Locomotion => Locomotion,
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null)
    };

    /// <summary>Parses an authored category name.</summary>
    public static bool TryParse(string? name, out AnimationCategory category)
    {
        switch (name)
        {
            case Idle:
                category = AnimationCategory.Idle;
                return true;
            case Locomotion:
                category = AnimationCategory.Locomotion;
                return true;
            default:
                category = default;
                return false;
        }
    }
}
