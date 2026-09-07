using System.Reflection;
using AlleyCat.IK;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.IK;

/// <summary>
/// Integration coverage for applicable, value-only IK target provider composition.
/// </summary>
public sealed partial class IKTargetIntentProviderCompositionIntegrationTests
{
    /// <summary>
    /// Verifies existing providers receive the canonical result without changing their zero-influence or transform-gating behaviour.
    /// </summary>
    [Fact]
    public void LegacyProvider_TryGetTargetIntentPreservesGetIntentAndTransformGating()
    {
        IKTargetIntent intent = new(new Transform3D(Basis.Identity, new Vector3(0.2f, 0.4f, -0.6f)), 0.0f);
        LegacyProvider provider = new(intent, shouldApplyTargetTransform: false);

        bool applies = provider.TryGetTargetIntent(out IKTargetIntentResolution resolution);

        Assert.True(applies);
        Assert.Equal(intent, resolution.Intent);
        Assert.False(resolution.ShouldApplyTargetTransform);
    }

    /// <summary>
    /// Verifies composition uses authored array order, skips refusals and non-finite successful values, and stops at the first valid result.
    /// </summary>
    [Fact]
    public async Task PriorityProvider_SelectsFirstApplicableFiniteResolutionInAuthoredOrder()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "PriorityProviderFixture",
        };
        RefusingProvider refusing = new()
        {
            Name = "Refusing",
        };
        ConfigurableProvider nonFinite = new()
        {
            Name = "NonFinite",
            Resolution = new IKTargetIntentResolution(
                new IKTargetIntent(new Transform3D(Basis.Identity, new Vector3(float.NaN, 0.0f, 0.0f)), 1.0f),
                true),
        };
        ConfigurableProvider firstApplicable = new()
        {
            Name = "FirstApplicable",
            Resolution = new IKTargetIntentResolution(
                new IKTargetIntent(new Transform3D(Basis.Identity, new Vector3(0.3f, 0.2f, -0.4f)), 0.65f),
                true),
        };
        ConfigurableProvider laterApplicable = new()
        {
            Name = "LaterApplicable",
            Resolution = new IKTargetIntentResolution(
                new IKTargetIntent(new Transform3D(Basis.Identity, new Vector3(-0.3f, 0.2f, -0.4f)), 0.8f),
                true),
        };
        PriorityIKTargetIntentProvider composite = new()
        {
            Name = "Composite",
            Providers = [refusing, nonFinite, firstApplicable, laterApplicable],
        };
        root.AddChild(refusing);
        root.AddChild(nonFinite);
        root.AddChild(firstApplicable);
        root.AddChild(laterApplicable);
        root.AddChild(composite);
        sceneTree.Root.AddChild(root);

        try
        {
            await WaitForNextFrameAsync(sceneTree);

            Assert.True(composite.TryGetTargetIntent(out IKTargetIntentResolution resolution));
            Assert.Equal(firstApplicable.Resolution, resolution);
            Assert.Equal(1, refusing.InvocationCount);
            Assert.Equal(1, nonFinite.InvocationCount);
            Assert.Equal(1, firstApplicable.InvocationCount);
            Assert.Equal(0, laterApplicable.InvocationCount);
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies a composite refuses when its ordered providers supply no applicable finite resolution.
    /// </summary>
    [Fact]
    public void PriorityProvider_WhenNoCandidateApplies_ReturnsFalse()
    {
        PriorityIKTargetIntentProvider composite = new()
        {
            Providers = [],
        };

        Assert.False(composite.TryGetTargetIntent(out IKTargetIntentResolution resolution));
        Assert.Equal(default, resolution);
    }

    /// <summary>
    /// Verifies an active grab override remains applicable and takes precedence when its default provider later refuses.
    /// </summary>
    [Headless]
    [Fact]
    public async Task HandGrabProvider_ActiveGrabOverrideRemainsApplicableWhenDefaultRefuses()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = new()
        {
            Name = "HandGrabProviderFixture",
        };
        ToggleProvider defaultProvider = new()
        {
            Name = "DefaultProvider",
            Resolution = new IKTargetIntentResolution(
                new IKTargetIntent(new Transform3D(Basis.Identity, new Vector3(-0.2f, 0.4f, 0.1f)), 0.25f),
                true),
        };
        ConfigurableProvider fallbackProvider = new()
        {
            Name = "FallbackProvider",
            Resolution = new IKTargetIntentResolution(
                new IKTargetIntent(new Transform3D(Basis.Identity, new Vector3(0.9f, 0.4f, 0.1f)), 0.6f),
                true),
        };
        HandGrabTargetProvider handProvider = new()
        {
            Name = "HandGrabProvider",
            DefaultProvider = defaultProvider,
            GrabInfluence = 0.8f,
            Responsiveness = 60.0f,
        };
        PriorityIKTargetIntentProvider composite = new()
        {
            Name = "Composite",
            Providers = [handProvider, fallbackProvider],
        };
        Transform3D grabTarget = new(Basis.Identity, new Vector3(0.25f, 0.45f, -0.15f));
        root.AddChild(defaultProvider);
        root.AddChild(fallbackProvider);
        root.AddChild(handProvider);
        root.AddChild(composite);
        sceneTree.Root.AddChild(root);

        try
        {
            await WaitForNextFrameAsync(sceneTree);
            Assert.True(handProvider.TryGetTargetIntent(out _));

            defaultProvider.IsApplicable = false;
            handProvider.SetGrabTarget(grabTarget);

            Assert.True(composite.TryGetTargetIntent(out IKTargetIntentResolution resolution));
            Assert.True(handProvider.IsGrabOverrideActive);
            Assert.Equal(grabTarget, handProvider.GrabTarget);
            Assert.Equal(0.8f, resolution.Intent.DesiredInfluence);
            Assert.True(resolution.ShouldApplyTargetTransform);
            Assert.Equal(0, fallbackProvider.InvocationCount);
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies the canonical result API provides only values rather than target, modifier, root, or skeleton nodes.
    /// </summary>
    [Fact]
    public void TargetIntentResolutionApi_IsValueOnly()
    {
        MethodInfo tryGetMethod = typeof(IKTargetIntentProvider).GetMethod(nameof(IKTargetIntentProvider.TryGetTargetIntent))
                                  ?? throw new InvalidOperationException("Expected canonical IK target intent API.");

        Assert.All(
            typeof(IKTargetIntentResolution).GetProperties(),
            property => Assert.False(typeof(Node).IsAssignableFrom(property.PropertyType)));
        Assert.All(
            tryGetMethod.GetParameters(),
            parameter => Assert.False(typeof(Node).IsAssignableFrom(parameter.ParameterType)));
    }

    private sealed partial class LegacyProvider(
        IKTargetIntent intent,
        bool shouldApplyTargetTransform) : IKTargetIntentProvider
    {
        public override bool ShouldApplyTargetTransform => shouldApplyTargetTransform;

        public override IKTargetIntent GetTargetIntent() => intent;
    }

    private sealed partial class RefusingProvider : IKTargetIntentProvider
    {
        public int InvocationCount
        {
            get; private set;
        }

        public override IKTargetIntent GetTargetIntent() => new(Transform3D.Identity, 0.0f);

        public override bool TryGetTargetIntent(out IKTargetIntentResolution resolution)
        {
            InvocationCount++;
            resolution = default;
            return false;
        }
    }

    private sealed partial class ConfigurableProvider : IKTargetIntentProvider
    {
        public int InvocationCount
        {
            get; private set;
        }

        public IKTargetIntentResolution Resolution
        {
            get; init;
        }

        public override IKTargetIntent GetTargetIntent() => Resolution.Intent;

        public override bool TryGetTargetIntent(out IKTargetIntentResolution resolution)
        {
            InvocationCount++;
            resolution = Resolution;
            return true;
        }
    }

    private sealed partial class ToggleProvider : IKTargetIntentProvider
    {
        public bool IsApplicable
        {
            get; set;
        } = true;

        public IKTargetIntentResolution Resolution
        {
            get; set;
        }

        public override IKTargetIntent GetTargetIntent() => Resolution.Intent;

        public override bool TryGetTargetIntent(out IKTargetIntentResolution resolution)
        {
            resolution = Resolution;
            return IsApplicable;
        }
    }
}
