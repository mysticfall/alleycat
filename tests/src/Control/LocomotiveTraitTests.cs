using AlleyCat.Component;
using AlleyCat.Control.Locomotion;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Control;

/// <summary>
/// Unit coverage for locomotion holder trait delegation.
/// </summary>
public sealed class LocomotiveTraitTests
{
    /// <summary>
    /// Holder trait methods delegate to the single held locomotion component.
    /// </summary>
    [Fact]
    public void ILocomotive_DefaultMethods_DelegateToLocomotionComponent()
    {
        var locomotion = new FakeLocomotion();
        ILocomotive holder = new FakeLocomotive(locomotion);

        holder.Move(new Vector2(0.25f, -0.5f));
        holder.Rotate(new Vector2(1.0f, 0.0f));

        Assert.Equal(new Vector2(0.25f, -0.5f), locomotion.LastMovementInput);
        Assert.Equal(new Vector2(1.0f, 0.0f), locomotion.LastRotationInput);
    }

    private sealed class FakeLocomotive(params IComponent[] components) : ILocomotive
    {
        public IReadOnlyList<IComponent> Components { get; } = components;
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
