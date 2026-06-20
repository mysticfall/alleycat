using AlleyCat.Body.Eyes;
using AlleyCat.Body.Hands;
using AlleyCat.Body.Voice;
using AlleyCat.Character;
using AlleyCat.Control.Locomotion;
using AlleyCat.Core;
using AlleyCat.Interaction;
using AlleyCat.Rigging;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Character;

/// <summary>
/// Unit coverage for the character aggregate trait contract.
/// </summary>
public sealed class ICharacterTests
{
    /// <summary>
    /// The character contract aggregates the holder traits required by a fully embodied humanoid.
    /// </summary>
    [Fact]
    public void ICharacter_AggregatesFullyEmbodiedHumanoidHolderTraits()
    {
        Assert.True(typeof(IComponentHolder).IsAssignableFrom(typeof(ICharacter)));
        Assert.True(typeof(IHasHands).IsAssignableFrom(typeof(ICharacter)));
        Assert.True(typeof(IEyesHolder).IsAssignableFrom(typeof(ICharacter)));
        Assert.True(typeof(IHasVoice).IsAssignableFrom(typeof(ICharacter)));
        Assert.True(typeof(ILocomotive).IsAssignableFrom(typeof(ICharacter)));
    }

    /// <summary>
    /// Inherited holder trait methods query the character component collection.
    /// </summary>
    [Fact]
    public void ICharacter_HolderTraitMethods_DelegateToComponents()
    {
        var leftHand = new FakeHand(LimbSide.Left);
        var eyes = new FakeEyes();
        var voice = new FakeVoice();
        var locomotion = new FakeLocomotion();
        ICharacter character = new FakeCharacter(leftHand, eyes, voice, locomotion);

        character.Move(new Vector2(0.5f, -0.25f));
        character.Rotate(new Vector2(-1.0f, 0.25f));

        Assert.True(character.TryGetHand(LimbSide.Left, out IHand? resolvedHand));
        Assert.Same(leftHand, resolvedHand);
        Assert.Same(eyes, character.RequireEyes());
        Assert.True(character.TryGetVoice(out IVoice? resolvedVoice));
        Assert.Same(voice, resolvedVoice);
        Assert.Equal(new Vector2(0.5f, -0.25f), locomotion.LastMovementInput);
        Assert.Equal(new Vector2(-1.0f, 0.25f), locomotion.LastRotationInput);
    }

    private sealed class FakeCharacter(params IComponent[] components) : ICharacter
    {
        public IReadOnlyList<IComponent> Components { get; } = components;
    }

    private sealed class FakeHand(LimbSide side) : IHand
    {
        public LimbSide Side => side;

        public IGrabbable? CurrentGrabbed => null;

        public IGrabbable? Grab() => null;

        public void Release()
        {
        }
    }

    private sealed class FakeEyes : IEyes
    {
        public Node3D? LookTarget
        {
            get; set;
        }

        public void SetLookTarget(Node3D? target) => LookTarget = target;

        public void ClearLookTarget() => LookTarget = null;
    }

    private sealed class FakeVoice : IVoice
    {
        public string Id => "fake";

        public Vector3 Origin => Vector3.Zero;

        public string? LastSpeech
        {
            get; private set;
        }

        public void Speak(string speech) => LastSpeech = speech;
    }

    private sealed class FakeLocomotion : ILocomotion
    {
        public Vector2 LastMovementInput
        {
            get; private set;
        }

        public Vector2 LastRotationInput
        {
            get; private set;
        }

        public void Move(Vector2 input) => LastMovementInput = input;

        public void Rotate(Vector2 input) => LastRotationInput = input;
    }
}
