using AlleyCat.Control.Locomotion;
using Godot;

namespace AlleyCat.IntegrationTests.Support;

/// <summary>
/// Resolves the delivered production content bindings (ANIM-002 delivery contract TR4) from the authoritative
/// composition wiring instead of test-authored literals, so automated tests stay catalogue-derived.
/// </summary>
public static class ProductionContentBindings
{
    private const string PlayerTemplatePath =
        "res://assets/characters/templates/reference_female/reference_female_player.tscn";

    /// <summary>
    /// Resolves the catalogue path of the production locomotion package bound to the delivered player locomotion.
    /// </summary>
    public static string ResolveReferenceFemaleLocomotionCataloguePath()
    {
        PackedScene scene = ResourceLoader.Load<PackedScene>(PlayerTemplatePath)
            ?? throw new InvalidOperationException(
                $"Selected player composition '{PlayerTemplatePath}' was not loadable.");
        using Node root = scene.Instantiate();
        return root.GetNode<CharacterLocomotion>("Locomotion").CataloguePath;
    }

    /// <summary>
    /// Resolves the catalogue path of the production idle package whose library the delivered player animation
    /// player binds as its <c>idle</c> library; the catalogue is co-located with that library.
    /// </summary>
    public static string ResolveReferenceFemaleIdleCataloguePath()
    {
        PackedScene scene = ResourceLoader.Load<PackedScene>(PlayerTemplatePath)
            ?? throw new InvalidOperationException(
                $"Selected player composition '{PlayerTemplatePath}' was not loadable.");
        using Node root = scene.Instantiate();
        AnimationPlayer player = root.FindChildren("*", nameof(AnimationPlayer), true, false).OfType<AnimationPlayer>().Single();
        string libraryPath = player.GetAnimationLibrary("idle")?.ResourcePath
            ?? throw new InvalidOperationException("The delivered player composition binds no idle animation library.");
        return $"{libraryPath[..^".tres".Length]}_catalogue.json";
    }

    /// <summary>
    /// Parses the production locomotion catalogue bound to the delivered player locomotion.
    /// </summary>
    public static StandingLocomotionCatalogue ParseReferenceFemaleLocomotionCatalogue()
    {
        string cataloguePath = ResolveReferenceFemaleLocomotionCataloguePath();
        return StandingLocomotionCatalogue.Parse(
            Godot.FileAccess.GetFileAsString(cataloguePath),
            cataloguePath);
    }
}
