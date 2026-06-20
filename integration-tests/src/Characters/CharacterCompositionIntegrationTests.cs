using AlleyCat.Body.Eyes;
using AlleyCat.Body.Hands;
using AlleyCat.Body.Voice;
using AlleyCat.Control.Locomotion;
using AlleyCat.Rigging;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

using CharacterHub = AlleyCat.Character.Character;

namespace AlleyCat.IntegrationTests.Characters;

/// <summary>
/// Godot-runtime coverage for the concrete character composition hub.
/// </summary>
public sealed class CharacterCompositionIntegrationTests
{
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

        Assert.Contains(nameof(CharacterHub.Locomotion), ex.Message, StringComparison.Ordinal);
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
        Assert.Same(character.Locomotion, character.Components[0]);
        Assert.Same(character.Eyes, character.Components[1]);
        Assert.Same(character.Voice, character.Components[2]);
        Assert.Same(character.LeftHand, character.Components[3]);
        Assert.Same(character.RightHand, character.Components[4]);
    }

    /// <summary>
    /// Refreshing replaces the previous cache deterministically after authoring changes.
    /// </summary>
    [Headless]
    [Fact]
    public void RefreshComponents_ReplacesCacheDeterministically()
    {
        CharacterHub character = CreateAuthoredCharacter();
        CharacterLocomotion firstLocomotion = character.Locomotion!;
        character.RefreshComponents();
        var replacementLocomotion = new CharacterLocomotion { Name = "replacement_locomotion" };
        character.AddChild(replacementLocomotion);

        character.Locomotion = replacementLocomotion;
        character.RefreshComponents();

        Assert.DoesNotContain(firstLocomotion, character.Components);
        Assert.Same(replacementLocomotion, character.Components[0]);
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

    private static CharacterHub CreateAuthoredCharacter()
    {
        var character = new CharacterHub { Name = "CharacterRoot" };
        var locomotion = new CharacterLocomotion { Name = "locomotion" };
        var eyes = new EyesBehaviour { Name = "eyes" };
        var voice = new TestVoice { Name = "voice", Id = "voice" };
        var leftHand = new HandPoseBehaviour { Name = "left_hand", Side = LimbSide.Left };
        var rightHand = new HandPoseBehaviour { Name = "right_hand", Side = LimbSide.Right };
        character.AddChild(locomotion);
        character.AddChild(eyes);
        character.AddChild(voice);
        character.AddChild(leftHand);
        character.AddChild(rightHand);
        character.Locomotion = locomotion;
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
