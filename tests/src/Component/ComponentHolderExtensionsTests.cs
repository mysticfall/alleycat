using AlleyCat.Core;
using Xunit;

namespace AlleyCat.Tests.Component;

/// <summary>
/// Unit coverage for deterministic component holder queries.
/// </summary>
public sealed class ComponentHolderExtensionsTests
{
    /// <summary>
    /// Component holders expose the service-provider contract through default interface dispatch.
    /// </summary>
    [Fact]
    public void GetService_InterfaceDefaultDispatch_ImplementsServiceProvider()
    {
        var expected = new PrimaryComponent();
        IComponentHolder holder = new FakeHolder(expected);

        IServiceProvider provider = holder;

        Assert.Same(expected, provider.GetService(typeof(PrimaryComponent)));
    }

    /// <summary>
    /// A null service type is rejected at the provider boundary.
    /// </summary>
    [Fact]
    public void GetService_NullType_ThrowsArgumentNullException()
    {
        IServiceProvider provider = new FakeHolder();

        _ = Assert.Throws<ArgumentNullException>(() => provider.GetService(null!));
    }

    /// <summary>
    /// Missing component services resolve to null.
    /// </summary>
    [Fact]
    public void GetService_NoMatches_ReturnsNull()
    {
        IServiceProvider provider = new FakeHolder(new SecondaryComponent());

        Assert.Null(provider.GetService(typeof(PrimaryComponent)));
    }

    /// <summary>
    /// Concrete and assignable capability requests return the exact component reference.
    /// </summary>
    [Fact]
    public void GetService_ConcreteAndCapabilityMatch_ReturnSameReference()
    {
        var expected = new PrimaryComponent();
        IServiceProvider provider = new FakeHolder(expected);

        Assert.Same(expected, provider.GetService(typeof(PrimaryComponent)));
        Assert.Same(expected, provider.GetService(typeof(IPrimaryCapability)));
        Assert.Same(expected, provider.GetService(typeof(object)));
    }

    /// <summary>
    /// One component may be resolved independently through each capability it implements.
    /// </summary>
    [Fact]
    public void GetService_MultiCapabilityComponent_ResolvesEachCapability()
    {
        var expected = new MultiCapabilityComponent("expected");
        IServiceProvider provider = new FakeHolder(expected);

        Assert.Same(expected, provider.GetService(typeof(IPrimaryCapability)));
        Assert.Same(expected, provider.GetService(typeof(ISecondaryCapability)));
    }

    /// <summary>
    /// Multiple assignable entries fail with requested type, holder, and cardinality context.
    /// </summary>
    [Fact]
    public void GetService_MultipleMatches_ThrowsWithContext()
    {
        IServiceProvider provider = new FakeHolder(new PrimaryComponent(), new MultiCapabilityComponent("second"));

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => provider.GetService(typeof(IPrimaryCapability)));

        Assert.Contains(typeof(IPrimaryCapability).FullName!, ex.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(FakeHolder).FullName!, ex.Message, StringComparison.Ordinal);
        Assert.Contains("Expected exactly 1, found 2", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Duplicate collection entries remain distinct matches even when they share a reference.
    /// </summary>
    [Fact]
    public void GetService_DuplicateReferenceEntries_ThrowsAsAmbiguous()
    {
        var duplicate = new PrimaryComponent();
        IServiceProvider provider = new FakeHolder(duplicate, duplicate);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => provider.GetService(typeof(PrimaryComponent)));

        Assert.Contains("Expected exactly 1, found 2", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Provider resolution considers component entries only, not the holder or provider itself.
    /// </summary>
    [Fact]
    public void GetService_NoMatchingComponent_DoesNotResolveHolderProviderOrObject()
    {
        IServiceProvider provider = new ComponentHolderComponent();

        Assert.Null(provider.GetService(typeof(IComponentHolder)));
        Assert.Null(provider.GetService(typeof(IServiceProvider)));
        Assert.Null(provider.GetService(typeof(IHolderCapability)));
        Assert.Null(provider.GetService(typeof(object)));
    }

    /// <summary>
    /// A nested holder is an ordinary component: it may match directly, but its own projection and provider are not
    /// traversed to find another capability.
    /// </summary>
    [Fact]
    public void GetService_NestedHolderProvider_OnlyResolvesDirectMatchWithoutTraversal()
    {
        var nestedCapability = new NestedCapabilityComponent();
        var nested = new SentinelNestedHolder(nestedCapability, nestedCapability);
        IServiceProvider provider = new FakeHolder(nested);

        Assert.Same(nested, provider.GetService(typeof(SentinelNestedHolder)));
        Assert.Null(provider.GetService(typeof(INestedCapability)));
        Assert.Equal(0, nested.ComponentsAccessCount);
        Assert.Equal(0, nested.ServiceRequestCount);
    }

    /// <summary>
    /// A provider component is never used as fallback for a capability absent from the holder's component projection.
    /// </summary>
    [Fact]
    public void GetService_MissingCapability_DoesNotUseProviderFallback()
    {
        var fallback = new NestedCapabilityComponent();
        var sentinelProvider = new SentinelProviderComponent(fallback);
        IServiceProvider provider = new FakeHolder(sentinelProvider);

        Assert.Null(provider.GetService(typeof(INestedCapability)));
        Assert.Equal(0, sentinelProvider.ServiceRequestCount);
    }

    /// <summary>
    /// A provider available to the holder but outside its component projection is not a global fallback source.
    /// </summary>
    [Fact]
    public void GetService_MissingCapability_DoesNotUseGlobalProviderFallback()
    {
        var fallback = new NestedCapabilityComponent();
        var globalProvider = new SentinelProviderComponent(fallback);
        IServiceProvider provider = new GlobalProviderAwareHolder(globalProvider);

        Assert.Null(provider.GetService(typeof(INestedCapability)));
        Assert.Equal(0, globalProvider.ServiceRequestCount);
    }

    /// <summary>
    /// Missing resolution does not activate or construct the requested component type.
    /// </summary>
    [Fact]
    public void GetService_MissingConstructibleComponent_DoesNotConstructCandidate()
    {
        ConstructibleComponent.ConstructionCount = 0;
        IServiceProvider provider = new FakeHolder();

        Assert.Null(provider.GetService(typeof(ConstructibleComponent)));
        Assert.Equal(0, ConstructibleComponent.ConstructionCount);
    }

    /// <summary>
    /// Every resolution observes the holder's current component projection and returns its exact reference rather than
    /// retaining a separate service cache.
    /// </summary>
    [Fact]
    public void GetService_RepeatedResolution_ObservesCurrentProjectionWithoutCaching()
    {
        var first = new PrimaryComponent();
        var second = new PrimaryComponent();
        var holder = new MutableHolder(first);
        IServiceProvider provider = holder;

        Assert.Same(first, provider.GetService(typeof(IPrimaryCapability)));
        Assert.Same(first, provider.GetService(typeof(IPrimaryCapability)));

        holder.SetComponents(second);

        Assert.Same(second, provider.GetService(typeof(IPrimaryCapability)));

        holder.SetComponents();

        Assert.Null(provider.GetService(typeof(IPrimaryCapability)));
        Assert.Equal(4, holder.ComponentsAccessCount);
    }

    /// <summary>
    /// Resolution is a query only and does not trigger disposal, lifecycle, registration, injection, or graph wiring.
    /// </summary>
    [Fact]
    public void GetService_ComponentWithSideEffectHooks_DoesNotInvokeHooks()
    {
        var expected = new SideEffectSentinelComponent();
        IServiceProvider provider = new FakeHolder(expected);

        Assert.Same(expected, provider.GetService(typeof(SideEffectSentinelComponent)));
        Assert.Equal(0, expected.DisposeCount);
        Assert.Equal(0, expected.LifecycleCount);
        Assert.Equal(0, expected.RegistrationCount);
        Assert.Equal(0, expected.InjectionCount);
        Assert.Equal(0, expected.GraphWiringCount);
    }

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

    private sealed class MutableHolder(params IComponent[] components) : IComponentHolder
    {
        public int ComponentsAccessCount
        {
            get; private set;
        }

        public IReadOnlyList<IComponent> Components
        {
            get
            {
                ComponentsAccessCount++;
                return field;
            }

            private set;
        } = components;

        public void SetComponents(params IComponent[] components) => Components = components;
    }

    private sealed class GlobalProviderAwareHolder(IServiceProvider globalProvider) : IComponentHolder
    {
        public IReadOnlyList<IComponent> Components { get; } = [];

        public IServiceProvider GlobalProvider { get; } = globalProvider;
    }

    private interface IHolderCapability
    {
    }

    private sealed class ComponentHolderComponent : IComponentHolder, IComponent, IHolderCapability
    {
        public IReadOnlyList<IComponent> Components { get; } = [];
    }

    private interface INestedCapability : IComponent
    {
    }

    private sealed class NestedCapabilityComponent : INestedCapability
    {
    }

    private sealed class SentinelNestedHolder(
        IComponent nestedComponent,
        object fallbackService) : IComponentHolder, IComponent
    {
        public int ComponentsAccessCount
        {
            get; private set;
        }

        public int ServiceRequestCount
        {
            get; private set;
        }

        public IReadOnlyList<IComponent> Components
        {
            get
            {
                ComponentsAccessCount++;
                return [nestedComponent];
            }
        }

        object? IServiceProvider.GetService(Type serviceType)
        {
            ServiceRequestCount++;
            return serviceType.IsInstanceOfType(fallbackService) ? fallbackService : null;
        }
    }

    private sealed class SentinelProviderComponent(object fallbackService) : IComponent, IServiceProvider
    {
        public int ServiceRequestCount
        {
            get; private set;
        }

        public object? GetService(Type serviceType)
        {
            ServiceRequestCount++;
            return serviceType.IsInstanceOfType(fallbackService) ? fallbackService : null;
        }
    }

    private sealed class ConstructibleComponent : IComponent
    {
        public ConstructibleComponent()
        {
            ConstructionCount++;
        }

        public static int ConstructionCount
        {
            get; set;
        }
    }

    private sealed class SideEffectSentinelComponent : IComponent, IDisposable
    {
        public int DisposeCount
        {
            get; private set;
        }

        public int LifecycleCount
        {
            get; private set;
        }

        public int RegistrationCount
        {
            get; private set;
        }

        public int InjectionCount
        {
            get; private set;
        }

        public int GraphWiringCount
        {
            get; private set;
        }

        public void Dispose() => DisposeCount++;

        public void StartLifecycle() => LifecycleCount++;

        public void Register() => RegistrationCount++;

        public void Inject() => InjectionCount++;

        public void WireGraph() => GraphWiringCount++;
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
