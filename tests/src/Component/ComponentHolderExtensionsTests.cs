using AlleyCat.Core;
using Xunit;

namespace AlleyCat.Tests.Component;

/// <summary>
/// Unit coverage for deterministic component holder queries.
/// </summary>
public sealed class ComponentHolderExtensionsTests
{
    /// <summary>
    /// Zero matches are not successful and leave the out component null.
    /// </summary>
    [Fact]
    public void TryGetComponent_NoMatches_ReturnsFalseAndNull()
    {
        var holder = new FakeHolder(new SecondaryComponent());

        bool found = holder.TryGetComponent(out PrimaryComponent? component);

        Assert.False(found);
        Assert.Null(component);
    }

    /// <summary>
    /// A single matching component is returned.
    /// </summary>
    [Fact]
    public void TryGetComponent_SingleMatch_ReturnsTrueAndComponent()
    {
        var expected = new PrimaryComponent();
        var holder = new FakeHolder(new SecondaryComponent(), expected);

        bool found = holder.TryGetComponent(out PrimaryComponent? component);

        Assert.True(found);
        Assert.Same(expected, component);
    }

    /// <summary>
    /// Multiple matches are ambiguous and must not pick the first match implicitly.
    /// </summary>
    [Fact]
    public void TryGetComponent_MultipleMatches_ReturnsFalseAndNull()
    {
        var holder = new FakeHolder(new PrimaryComponent(), new PrimaryComponent());

        bool found = holder.TryGetComponent(out PrimaryComponent? component);

        Assert.False(found);
        Assert.Null(component);
    }

    /// <summary>
    /// Matching components are returned in holder-defined order and support assignable capability interfaces.
    /// </summary>
    [Fact]
    public void GetComponents_AssignableMatches_ReturnsHolderOrder()
    {
        var first = new MultiCapabilityComponent("first");
        var second = new PrimaryComponent();
        var third = new MultiCapabilityComponent("third");
        var holder = new FakeHolder(new SecondaryComponent(), first, second, third);

        IReadOnlyList<IPrimaryCapability> components = holder.GetComponents<IPrimaryCapability>();

        Assert.Equal(3, components.Count);
        Assert.Same(first, components[0]);
        Assert.Same(second, components[1]);
        Assert.Same(third, components[2]);
    }

    /// <summary>
    /// A required single component is returned.
    /// </summary>
    [Fact]
    public void RequireComponent_SingleMatch_ReturnsComponent()
    {
        var expected = new PrimaryComponent();
        var holder = new FakeHolder(expected);

        PrimaryComponent component = holder.RequireComponent<PrimaryComponent>();

        Assert.Same(expected, component);
    }

    /// <summary>
    /// Missing required components fail fast with requested and holder type context.
    /// </summary>
    [Fact]
    public void RequireComponent_NoMatches_ThrowsWithContext()
    {
        var holder = new FakeHolder(new SecondaryComponent());

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            holder.RequireComponent<PrimaryComponent>);

        Assert.Contains(typeof(PrimaryComponent).FullName!, ex.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(FakeHolder).FullName!, ex.Message, StringComparison.Ordinal);
        Assert.Contains("Expected exactly 1, found 0", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ambiguous required components fail fast rather than hiding ambiguity.
    /// </summary>
    [Fact]
    public void RequireComponent_MultipleMatches_ThrowsWithContext()
    {
        var holder = new FakeHolder(new PrimaryComponent(), new PrimaryComponent());

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            holder.RequireComponent<PrimaryComponent>);

        Assert.Contains(typeof(PrimaryComponent).FullName!, ex.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(FakeHolder).FullName!, ex.Message, StringComparison.Ordinal);
        Assert.Contains("Expected exactly 1, found 2", ex.Message, StringComparison.Ordinal);
    }

    private sealed class FakeHolder(params IComponent[] components) : IComponentHolder
    {
        public IReadOnlyList<IComponent> Components { get; } = components;
    }

    private interface IPrimaryCapability : IComponent
    {
    }

    private interface ISecondaryCapability : IComponent
    {
    }

    private sealed class PrimaryComponent : IPrimaryCapability
    {
    }

    private sealed class SecondaryComponent : ISecondaryCapability
    {
    }

    private sealed class MultiCapabilityComponent(string name) : IPrimaryCapability, ISecondaryCapability
    {
        public string Name { get; } = name;
    }
}
