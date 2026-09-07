using AlleyCat.Control.Locomotion;
using AlleyCat.Core;
using AlleyCat.Interaction.Hands;
using AlleyCat.Navigation;
using AlleyCat.Rigging;
using AlleyCat.Speech.Voice;
using AlleyCat.TestFramework;
using AlleyCat.Vision;
using Godot;
using Xunit;
using CharacterContract = AlleyCat.Character.ICharacter;
using CharacterHub = AlleyCat.Character.Character;
using PlayerContract = AlleyCat.Character.IPlayer;
using PlayerRoot = AlleyCat.Character.PlayerCharacter;

namespace AlleyCat.IntegrationTests.Characters;

/// <summary>
/// Godot-runtime coverage for the concrete character composition hub.
/// </summary>
public sealed class CharacterCompositionIntegrationTests
{
    private const string ReferenceFemaleNPCScenePath =
        "res://assets/characters/templates/reference_female/reference_female_npc.tscn";

    private const string ReferenceMaleNPCScenePath =
        "res://assets/characters/templates/reference_male/reference_male_npc.tscn";

    /// <summary>
    /// Imported runtime roots may enter the tree before installers copy and rebase explicit capability references.
    /// </summary>
    [Headless]
    [Fact]
    public void Ready_UnwiredCharacter_DoesNotValidateBeforeInstallerFinalisation()
    {
        var character = new CharacterHub { Name = "CharacterRoot" };

        Exception? exception = Record.Exception(character._Ready);

        Assert.Null(exception);
        Assert.Empty(character.Components);
    }

    /// <summary>
    /// Explicit refresh remains the final validation boundary for missing required references.
    /// </summary>
    [Headless]
    [Fact]
    public void RefreshComponents_UnwiredCharacter_ThrowsClearAuthoringError()
    {
        var character = new CharacterHub { Name = "CharacterRoot" };

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(character.RefreshComponents);

        Assert.Contains(nameof(CharacterHub.Eyes), ex.Message, StringComparison.Ordinal);
        Assert.Contains("null", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Refreshing projects explicit capability references in the stable character order.
    /// </summary>
    [Headless]
    [Fact]
    public void RefreshComponents_ProjectsExplicitReferencesInStableCapabilityOrder()
    {
        CharacterHub character = CreateAuthoredCharacter();

        character.RefreshComponents();

        Assert.Equal(5, character.Components.Count);
        Assert.Same(character.Eyes, character.Components[0]);
        Assert.Same(character.Navigation, character.Components[1]);
        Assert.Same(character.Voice, character.Components[2]);
        Assert.Same(character.LeftHand, character.Components[3]);
        Assert.Same(character.RightHand, character.Components[4]);
        Assert.Same(character.Navigation, ((CharacterContract)character).RequireNavigation());
    }

    /// <summary>
    /// Player composition projects every capability in the exact CHAR-002 order.
    /// </summary>
    [Headless]
    [Fact]
    public void PlayerCharacter_ProjectsExactRoleCapabilityOrder()
    {
        var player = new PlayerRoot { Name = "PlayerRoot" };
        var navigation = new PlayerNavigation { Name = "navigation" };
        var eyes = new EyesBehaviour { Name = "eyes" };
        var voice = new TestVoice { Name = "voice", Id = "voice" };
        var leftHand = new HandPoseBehaviour { Name = "left_hand", Side = LimbSide.Left };
        var rightHand = new HandPoseBehaviour { Name = "right_hand", Side = LimbSide.Right };
        var locomotion = new CharacterLocomotion { Name = "locomotion" };
        player.AddChild(navigation);
        player.AddChild(eyes);
        player.AddChild(voice);
        player.AddChild(leftHand);
        player.AddChild(rightHand);
        player.AddChild(locomotion);
        player.Navigation = navigation;
        player.Eyes = eyes;
        player.Voice = voice;
        player.LeftHand = leftHand;
        player.RightHand = rightHand;
        player.Locomotion = locomotion;

        player.RefreshComponents();

        Assert.Equal(6, player.Components.Count);
        Assert.Same(eyes, player.Components[0]);
        Assert.Same(locomotion, player.Components[1]);
        Assert.Same(navigation, player.Components[2]);
        Assert.Same(voice, player.Components[3]);
        Assert.Same(leftHand, player.Components[4]);
        Assert.Same(rightHand, player.Components[5]);
        PlayerContract contract = Assert.IsAssignableFrom<PlayerContract>(player);
        Assert.Same(eyes, contract.RequireVision());
        Assert.Same(locomotion, contract.RequireComponent<ILocomotion>());
        Assert.Same(navigation, contract.RequireNavigation());
        Assert.Same(voice, contract.RequireVoice());
        Assert.True(contract.TryGetHand(LimbSide.Left, out IHand? resolvedLeft));
        Assert.Same(leftHand, resolvedLeft);
        Assert.True(contract.TryGetHand(LimbSide.Right, out IHand? resolvedRight));
        Assert.Same(rightHand, resolvedRight);
        Assert.False(navigation.IsMovementAuthorityActive);
    }

    /// <summary>
    /// Refreshing replaces the previous cache deterministically after authoring changes.
    /// </summary>
    [Headless]
    [Fact]
    public void RefreshComponents_ReplacesCacheDeterministically()
    {
        CharacterHub character = CreateAuthoredCharacter();
        NavigationBase firstNavigation = character.Navigation!;
        character.RefreshComponents();
        var replacementNavigation = new DirectTransformNavigation { Name = "replacement_navigation" };
        character.AddChild(replacementNavigation);

        character.Navigation = replacementNavigation;
        character.RefreshComponents();

        Assert.DoesNotContain(firstNavigation, character.Components);
        Assert.Same(replacementNavigation, character.Components[1]);
    }

    /// <summary>
    /// The character hub does not recursively discover child or descendant components outside explicit references.
    /// </summary>
    [Headless]
    [Fact]
    public void RefreshComponents_DoesNotDiscoverUnreferencedDescendants()
    {
        CharacterHub character = CreateAuthoredCharacter();
        var unreferencedVoice = new TestVoice { Name = "unreferenced_voice", Id = "unreferenced_voice" };
        var container = new Node { Name = "container" };
        var unreferencedDescendantVoice = new TestVoice { Name = "unreferenced_descendant_voice", Id = "unreferenced_descendant_voice" };
        character.AddChild(unreferencedVoice);
        character.AddChild(container);
        container.AddChild(unreferencedDescendantVoice);

        character.RefreshComponents();

        Assert.DoesNotContain(unreferencedVoice, character.Components);
        Assert.DoesNotContain(unreferencedDescendantVoice, character.Components);
    }

    /// <summary>
    /// Missing voice references fail fast with property context.
    /// </summary>
    [Headless]
    [Fact]
    public void RefreshComponents_MissingVoice_ThrowsClearAuthoringError()
    {
        CharacterHub character = CreateAuthoredCharacter();
        character.Voice = null;

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(character.RefreshComponents);

        Assert.Contains(nameof(CharacterHub.Voice), ex.Message, StringComparison.Ordinal);
        Assert.Contains("null", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Missing references fail fast with property context.
    /// </summary>
    [Headless]
    [Fact]
    public void RefreshComponents_MissingReference_ThrowsClearAuthoringError()
    {
        CharacterHub character = CreateAuthoredCharacter();
        character.Eyes = null;

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(character.RefreshComponents);

        Assert.Contains(nameof(CharacterHub.Eyes), ex.Message, StringComparison.Ordinal);
        Assert.Contains("null", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Hand references must match their authored side.
    /// </summary>
    [Headless]
    [Fact]
    public void RefreshComponents_WrongSideHand_ThrowsClearAuthoringError()
    {
        CharacterHub character = CreateAuthoredCharacter();
        character.LeftHand!.Side = LimbSide.Right;

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(character.RefreshComponents);

        Assert.Contains(nameof(CharacterHub.LeftHand), ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(LimbSide.Left), ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(LimbSide.Right), ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The reference female NPC template authors production locomotive navigation bound to its character root.
    /// </summary>
    [Headless]
    [Fact]
    public void ReferenceFemaleNPCTemplate_AuthorsNPCNavigation()
        => AssertNPCTemplateAuthorsNPCNavigation(ReferenceFemaleNPCScenePath);

    /// <summary>
    /// The reference male NPC template authors production locomotive navigation bound to its character root.
    /// </summary>
    [Headless]
    [Fact]
    public void ReferenceMaleNPCTemplate_AuthorsNPCNavigation()
        => AssertNPCTemplateAuthorsNPCNavigation(ReferenceMaleNPCScenePath);

    private static void AssertNPCTemplateAuthorsNPCNavigation(string scenePath)
    {
        PackedScene scene = ResourceLoader.Load<PackedScene>(scenePath);
        CharacterHub character = Assert.IsType<CharacterHub>(scene.Instantiate(), exactMatch: false);
        try
        {
            NPCNavigation navigation = Assert.IsType<NPCNavigation>(character.Navigation);
            Assert.Same(character, navigation.Actor);
            Assert.Same(character.GetNode<AnimationTree>("AnimationTree"), navigation.AnimationTree);
            Assert.Same(character.GetNode<Node3D>(character.Name.ToString()), navigation.RootMotionReference);
            Assert.True(navigation.AwaitInstallerActivation);
            Assert.False(navigation.IsRuntimeBindingActive);
            Assert.False(navigation.IsPhysicsProcessing());
            Assert.Equal(0, navigation.PhysicalStepCount);
            Assert.IsNotAssignableFrom<ILocomotive>(character);
            Assert.Empty(character.GetChildren().OfType<CharacterLocomotion>());
            _ = Assert.Single(character.GetChildren().OfType<NavigationBase>());
        }
        finally
        {
            character.QueueFree();
        }
    }

    private static CharacterHub CreateAuthoredCharacter()
    {
        var character = new CharacterHub { Name = "CharacterRoot" };
        var navigation = new DirectTransformNavigation { Name = "navigation" };
        var eyes = new EyesBehaviour { Name = "eyes" };
        var voice = new TestVoice { Name = "voice", Id = "voice" };
        var leftHand = new HandPoseBehaviour { Name = "left_hand", Side = LimbSide.Left };
        var rightHand = new HandPoseBehaviour { Name = "right_hand", Side = LimbSide.Right };
        character.AddChild(navigation);
        character.AddChild(eyes);
        character.AddChild(voice);
        character.AddChild(leftHand);
        character.AddChild(rightHand);
        character.Navigation = navigation;
        character.Eyes = eyes;
        character.Voice = voice;
        character.LeftHand = leftHand;
        character.RightHand = rightHand;
        return character;
    }

    private sealed partial class TestVoice : Voice
    {
        public override void Speak(string speech)
        {
        }
    }
}
