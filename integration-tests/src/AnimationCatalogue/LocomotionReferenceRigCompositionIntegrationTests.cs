using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.AnimationCatalogue;

/// <summary>Restricts production-rig coverage to animation-host wiring, not authored animation behaviour.</summary>
public sealed class LocomotionReferenceRigCompositionIntegrationTests
{
    private const string PlayerScenePath = "res://assets/characters/reference/ally_player.tscn";

    /// <inheritdoc/>
    [Fact]
    public void ReferenceRigComposition_ContainsAnimationPlayer()
    {
        PackedScene scene = ResourceLoader.Load<PackedScene>(PlayerScenePath)
            ?? throw new Xunit.Sdk.XunitException($"Could not load production composition scene '{PlayerScenePath}'.");
        Node root = scene.Instantiate();
        try
        {
            AnimationPlayer player = root.GetNode<AnimationPlayer>("AnimationPlayer");
            Assert.NotEmpty(player.GetAnimationLibraryList());
        }
        finally
        {
            root.Free();
        }
    }
}
