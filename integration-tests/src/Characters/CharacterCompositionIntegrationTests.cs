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

        Assert.Equal(4, character.Components.Count);
        Assert.Same(character.Eyes, character.Components[0]);
        Assert.Same(character.Voice, character.Components[1]);
        Assert.Same(character.LeftHand, character.Components[2]);
        Assert.Same(character.RightHand, character.Components[3]);
        Assert.False(character.TryGetComponent<INavigation>(out _));
    }

    /// <summary>
    /// Player composition projects every capability in the exact CHAR-002 order.
    /// </summary>
    [Headless]
    [Fact]
    public void PlayerCharacter_ProjectsExactRoleCapabilityOrder()
    {
        var player = new PlayerRoot { Name = "PlayerRoot" };
        var eyes = new EyesBehaviour { Name = "eyes" };
        var voice = new TestVoice { Name = "voice", Id = "voice" };
        var leftHand = new HandPoseBehaviour { Name = "left_hand", Side = LimbSide.Left };
        var rightHand = new HandPoseBehaviour { Name = "right_hand", Side = LimbSide.Right };
        player.AddChild(eyes);
        player.AddChild(voice);
        player.AddChild(leftHand);
        player.AddChild(rightHand);
        player.Eyes = eyes;
        player.Voice = voice;
        player.LeftHand = leftHand;
        player.RightHand = rightHand;

        player.RefreshComponents();

        Assert.Equal(4, player.Components.Count);
        Assert.Same(eyes, player.Components[0]);
        Assert.Same(voice, player.Components[1]);
        Assert.Same(leftHand, player.Components[2]);
        Assert.Same(rightHand, player.Components[3]);
        PlayerContract contract = Assert.IsAssignableFrom<PlayerContract>(player);
        Assert.Same(eyes, contract.RequireVision());
        Assert.False(contract.TryGetComponent<ILocomotion>(out _));
        Assert.False(contract.TryGetComponent<INavigation>(out _));
        Assert.Same(voice, contract.RequireVoice());
        Assert.True(contract.TryGetHand(LimbSide.Left, out IHand? resolvedLeft));
        Assert.Same(leftHand, resolvedLeft);
        Assert.True(contract.TryGetHand(LimbSide.Right, out IHand? resolvedRight));
        Assert.Same(rightHand, resolvedRight);
    }

    /// <summary>
    /// Refreshing replaces the previous cache deterministically after authoring changes.
    /// </summary>
    [Headless]
    [Fact]
    public void RefreshComponents_ReplacesCacheDeterministically()
    {
        CharacterHub character = CreateAuthoredCharacter();
        Voice firstVoice = character.Voice!;
        character.RefreshComponents();
        var replacementVoice = new TestVoice { Name = "replacement_voice", Id = "replacement_voice" };
        character.AddChild(replacementVoice);

        character.Voice = replacementVoice;
        character.RefreshComponents();

        Assert.DoesNotContain(firstVoice, character.Components);
        Assert.Same(replacementVoice, character.Components[1]);
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
    /// The reference female NPC template authors production <c>MotionBricksNavigation</c> as its character navigation
    /// (CHAR-002 TR12/TR39, NAV-001 TR5) without a player <see cref="ILocomotive"/> or locomotion dependency.
    /// </summary>
    [Headless]
    [Fact]
    public void ReferenceFemaleNPCTemplate_AuthorsMotionBricksNavigation()
    {
        PackedScene scene = ResourceLoader.Load<PackedScene>(ReferenceFemaleNPCScenePath);
        CharacterHub character = Assert.IsType<CharacterHub>(scene.Instantiate(), exactMatch: false);
        try
        {
            MotionBricksNavigation navigation = Assert.IsType<MotionBricksNavigation>(character.Navigation);
            Assert.Same(character.GetNode("MotionBricksNavigation"), navigation);
            Assert.Same(navigation, Assert.Single(character.GetChildren().OfType<NavigationBase>()));
            Assert.IsNotAssignableFrom<ILocomotive>(character);
            Assert.Empty(character.GetChildren().OfType<ILocomotion>());
            Assert.NotNull(character.Eyes);
            Assert.NotNull(character.LeftHand);
            Assert.NotNull(character.RightHand);
        }
        finally
        {
            character.QueueFree();
        }
    }

    /// <summary>
    /// The reference male NPC leaves navigation absent while replacement integration is pending.
    /// </summary>
    [Headless]
    [Fact]
    public void ReferenceMaleNPCTemplate_DefersNavigation()
        => AssertNPCTemplateDefersNavigation(ReferenceMaleNPCScenePath);

    private static void AssertNPCTemplateDefersNavigation(string scenePath)
    {
        PackedScene scene = ResourceLoader.Load<PackedScene>(scenePath);
        CharacterHub character = Assert.IsType<CharacterHub>(scene.Instantiate(), exactMatch: false);
        try
        {
            Assert.Null(character.Navigation);
            Assert.IsNotAssignableFrom<ILocomotive>(character);
            Assert.Empty(character.GetChildren().OfType<NavigationBase>());
            Assert.NotNull(character.Eyes);
            Assert.NotNull(character.LeftHand);
            Assert.NotNull(character.RightHand);
        }
        finally
        {
            character.QueueFree();
        }
    }

    private static CharacterHub CreateAuthoredCharacter()
    {
        var character = new CharacterHub { Name = "CharacterRoot" };
        var eyes = new EyesBehaviour { Name = "eyes" };
        var voice = new TestVoice { Name = "voice", Id = "voice" };
        var leftHand = new HandPoseBehaviour { Name = "left_hand", Side = LimbSide.Left };
        var rightHand = new HandPoseBehaviour { Name = "right_hand", Side = LimbSide.Right };
        character.AddChild(eyes);
        character.AddChild(voice);
        character.AddChild(leftHand);
        character.AddChild(rightHand);
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
