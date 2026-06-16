using System.Reflection;
using AlleyCat.Core.Installer;
using AlleyCat.Rigging.Installation;
using AlleyCat.Rigging.Physics;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Core.Installer;

/// <summary>
/// Unit coverage for installer editor authoring contracts.
/// </summary>
public sealed class InstallerEditorToolTests
{
    /// <summary>
    /// Editor refresh depends on tool-enabled installer nodes so configured child installers are discoverable and callable.
    /// </summary>
    [Fact]
    public void InstallerTypesUsedByEditorRefresh_AreToolEnabled()
    {
        Type[] installerTypes =
        [
            typeof(SceneInstaller),
            typeof(RigRoleTemplateSceneInstaller),
            typeof(RigTemplateSubtreeInstaller),
            typeof(DynamicPhysicalRigTemplateInstaller),
        ];

        foreach (Type installerType in installerTypes)
        {
            Assert.NotNull(installerType.GetCustomAttribute<ToolAttribute>());
        }
    }

    /// <summary>
    /// The generic installation context remains free of template-only data and type-erased service dictionaries.
    /// </summary>
    [Fact]
    public void SceneInstallationContext_PublicAPI_ContainsOnlyGenericInstallerState()
    {
        Type contextType = typeof(SceneInstallationContext);

        Assert.Null(contextType.GetProperty("TemplateRoot"));
        Assert.Null(contextType.GetMethod("WithTemplateRoot"));
        Assert.Null(contextType.GetMethod("WithService"));
        Assert.Null(contextType.GetMethod("TryGetService"));
        Assert.NotNull(contextType.GetProperty(nameof(SceneInstallationContext.TargetRoot)));
        Assert.NotNull(contextType.GetProperty(nameof(SceneInstallationContext.MetadataNamespace)));
    }

    /// <summary>
    /// Installer-specific dependencies are expressed through typed context interfaces instead of ambient services.
    /// </summary>
    [Fact]
    public void InstallerInterfaces_ExposeTypedContextPathForTemplateAndCharacterInstallers()
    {
        Assert.Contains(
            typeof(ISceneInstaller<RigInstallationContext>),
            typeof(RigSceneInstaller).GetInterfaces());
        Assert.True(typeof(RigInstallationContext).IsSubclassOf(typeof(TemplateSceneInstallationContext)));
        Assert.NotNull(typeof(TemplateSceneInstallationContext).GetProperty(nameof(TemplateSceneInstallationContext.TemplateRoot)));
        Assert.NotNull(typeof(RigInstallationContext).GetProperty(nameof(RigInstallationContext.Skeleton)));
    }

    /// <summary>
    /// Automatic runtime installation is kept on root/coordinator installers instead of duplicated by child subsystem bases.
    /// </summary>
    [Fact]
    public void AutoInstallOnReady_IsNotDuplicatedByRigSubsystemInstallers()
    {
        Assert.Null(typeof(RigSubsystemInstaller).GetProperty("AutoInstallOnReady"));
        Assert.NotNull(typeof(RigRoleTemplateSceneInstaller).GetProperty("AutoInstallOnReady"));
    }
}
