using AlleyCat.Body.Eyes;
using AlleyCat.Component;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Body.Eyes;

/// <summary>
/// Unit coverage for BODY-004 Eyes holder trait discovery.
/// </summary>
public sealed class EyesHolderTests
{
    /// <summary>
    /// Verifies IEyesHolder resolves eyes through the component holder conventions.
    /// </summary>
    [Fact]
    public void IEyesHolder_DefaultMethods_ResolveEyesComponent()
    {
        var eyes = new FakeEyes();
        IEyesHolder holder = new FakeEyesHolder(eyes);

        Assert.True(holder.TryGetEyes(out IEyes? resolved));
        Assert.Same(eyes, resolved);
        Assert.Same(eyes, holder.RequireEyes());
    }

    private sealed class FakeEyesHolder(params IComponent[] components) : IEyesHolder
    {
        public IReadOnlyList<IComponent> Components { get; } = components;
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
}
