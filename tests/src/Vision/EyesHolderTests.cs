using AlleyCat.Core;
using AlleyCat.Sense;
using AlleyCat.Vision;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Vision;

/// <summary>
/// Unit coverage for VISION-001 Eyes holder trait discovery.
/// </summary>
public sealed class EyesHolderTests
{
    /// <summary>
    /// Verifies IHasVision resolves vision through the component holder conventions.
    /// </summary>
    [Fact]
    public void IHasVision_DefaultMethods_ResolveVisionComponent()
    {
        var eyes = new FakeEyes();
        IHasVision holder = new FakeEyesHolder(eyes);

        Assert.True(holder.TryGetVision(out IVision? resolved));
        Assert.Same(eyes, resolved);
        Assert.Same(eyes, holder.RequireVision());
    }

    private sealed class FakeEyesHolder(params IComponent[] components) : IHasVision
    {
        public IReadOnlyList<IComponent> Components { get; } = components;
    }

    private sealed class FakeEyes : IVision
    {
        public Node3D? LookTarget
        {
            get; set;
        }

        public IReadOnlyList<Type> PerceptTypes => throw new NotImplementedException();

        public event Action<IPercept>? Perceived
        {
            add
            {
            }
            remove
            {
            }
        }

        public void SetLookTarget(Node3D? target) => LookTarget = target;

        public void ClearLookTarget() => LookTarget = null;

        public static IReadOnlyList<VisualScanResult> Scan() => [];
    }
}
