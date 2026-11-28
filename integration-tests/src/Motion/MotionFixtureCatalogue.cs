using AlleyCat.Animations;
using AlleyCat.IntegrationTests.Animations;

namespace AlleyCat.IntegrationTests.Motion;

/// <summary>
/// Builds the animation catalogue a hand-assembled MotionBricks fixture character needs (ANIM-003 TR6–TR10): locomotion
/// and idles resolve through the character's catalogue, mirroring the reference templates' selected variants and defaults.
/// </summary>
internal static class MotionFixtureCatalogue
{

    /// <summary>
    /// Creates the catalogue: the base template's locomotion default, plus the NPC role's idle default when
    /// <paramref name="npc" /> is set (player templates declare no idle default).
    /// </summary>
    public static AnimationCatalogue Create(bool npc)
    {
        AnimationCatalogue catalogue = new()
        {
            Name = "AnimationCatalogue",
            DefaultLocomotion = "feminine",
            DefaultIdle = npc ? "sad" : string.Empty,
        };
        catalogue.AddChild(AnimationFixtureVariants.Layer("FixtureBase",
            AnimationFixtureVariants.Add(AnimationFixtureVariants.Sad), AnimationFixtureVariants.Add(AnimationFixtureVariants.Feminine)));
        return catalogue;
    }
}
