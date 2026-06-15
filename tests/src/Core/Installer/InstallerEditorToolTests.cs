using System.Reflection;
using AlleyCat.Body;
using AlleyCat.Character.Installer;
using AlleyCat.Core.Installer;
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
            typeof(CharacterRoleTemplateSceneInstaller),
            typeof(CharacterTemplateSubtreeInstaller),
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
            typeof(ISceneInstaller<CharacterInstallationContext>),
            typeof(CharacterSceneInstaller).GetInterfaces());
        Assert.True(typeof(CharacterInstallationContext).IsSubclassOf(typeof(TemplateSceneInstallationContext)));
        Assert.NotNull(typeof(TemplateSceneInstallationContext).GetProperty(nameof(TemplateSceneInstallationContext.TemplateRoot)));
        Assert.NotNull(typeof(CharacterInstallationContext).GetProperty(nameof(CharacterInstallationContext.Skeleton)));
    }

    /// <summary>
    /// Automatic runtime installation is kept on root/coordinator installers instead of duplicated by child subsystem bases.
    /// </summary>
    [Fact]
    public void AutoInstallOnReady_IsNotDuplicatedByCharacterSubsystemInstallers()
    {
        Assert.Null(typeof(CharacterSubsystemInstaller).GetProperty("AutoInstallOnReady"));
        Assert.NotNull(typeof(CharacterRoleTemplateSceneInstaller).GetProperty("AutoInstallOnReady"));
    }
}
