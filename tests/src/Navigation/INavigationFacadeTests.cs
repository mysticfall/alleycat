using System.Reflection;
using AlleyCat.Navigation;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Navigation;

/// <summary>
/// Unit coverage for the public navigation component facade contract.
/// </summary>
public sealed class INavigationFacadeTests
{
    /// <summary>
    /// Verifies NAV-001 keeps the Godot NavigationAgent3D node behind the facade boundary.
    /// </summary>
    [Fact]
    public void PublicMembers_DoNotExposeNavigationAgent3D()
    {
        Type agentType = typeof(NavigationAgent3D);
        IEnumerable<MemberInfo> exposedAgentMembers = typeof(INavigation)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Where(member => ExposesType(member, agentType));

        Assert.Empty(exposedAgentMembers.Select(member => member.Name));
    }

    /// <summary>
    /// Verifies the shared Godot-backed base is itself the NavigationAgent3D used for path state.
    /// </summary>
    [Fact]
    public void NavigationBase_InheritsNavigationAgent3D()
        => Assert.True(typeof(NavigationAgent3D).IsAssignableFrom(typeof(NavigationBase)));

    /// <summary>
    /// Verifies the facade uses destination terminology for navigation intent.
    /// </summary>
    [Fact]
    public void PublicMembers_ExposeDestinationTerminology()
    {
        Type navigationType = typeof(INavigation);

        Assert.NotNull(navigationType.GetProperty(nameof(INavigation.Destination)));
        Assert.NotNull(navigationType.GetProperty(nameof(INavigation.HasDestination)));
        Assert.NotNull(navigationType.GetProperty(nameof(INavigation.DestinationReachedDistance)));
        Assert.NotNull(navigationType.GetMethod(nameof(INavigation.SetDestination)));
        Assert.NotNull(navigationType.GetMethod(nameof(INavigation.ClearDestination)));
    }

    /// <summary>
    /// Verifies facade-facing navigation intent members avoid legacy target terminology.
    /// </summary>
    [Fact]
    public void FacadePublicMembers_DoNotExposeTargetTerminology()
    {
        AssertNoTargetNamedMembers(typeof(INavigation));
        AssertNoTargetNamedMembers(typeof(INavigator));
    }

    /// <summary>
    /// Verifies the concrete direct-transform implementation keeps its intentionally exported moved-node Target.
    /// </summary>
    [Fact]
    public void DirectTransformNavigation_ExposesOnlyIntentionalDeclaredTargetMember()
    {
        MemberInfo[] targetMembers = typeof(DirectTransformNavigation)
            .GetMember(nameof(DirectTransformNavigation.Target), BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        MemberInfo targetMember = Assert.Single(targetMembers);
        Assert.True(targetMember is PropertyInfo);
    }

    private static bool ExposesType(MemberInfo member, Type exposedType) => member switch
    {
        PropertyInfo property => ContainsType(property.PropertyType, exposedType),
        MethodInfo method => ContainsType(method.ReturnType, exposedType)
            || method.GetParameters().Any(parameter => ContainsType(parameter.ParameterType, exposedType)),
        _ => false,
    };

    private static bool ContainsType(Type type, Type exposedType) => type == exposedType
        || (type.IsArray && ContainsType(type.GetElementType()!, exposedType))
        || (type.IsGenericType
            && type.GetGenericArguments().Any(argument => ContainsType(argument, exposedType)));

    private static void AssertNoTargetNamedMembers(Type facadeType)
    {
        IEnumerable<string> targetNamedMembers = facadeType
            .GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Select(member => member.Name)
            .Where(name => name.Contains("Target", StringComparison.Ordinal));

        Assert.Empty(targetNamedMembers);
    }
}
