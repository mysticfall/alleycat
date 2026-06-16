using AlleyCat.Core.Installer;
using Godot;

namespace AlleyCat.Rigging.Installation;

/// <summary>
/// Base class for rig-specific installers that resolve shared character dependencies from CORE-005 context.
/// </summary>
[GlobalClass]
public abstract partial class RigSceneInstaller : SceneInstaller, ISceneInstaller<RigInstallationContext>
{
    /// <summary>
    /// Installs this rig module using explicit template and skeleton dependencies.
    /// </summary>
    public abstract SceneInstallationResult Install(RigInstallationContext context);

    /// <inheritdoc />
    public override SceneInstallationResult Install(SceneInstallationContext context)
        => context is RigInstallationContext characterContext
            ? Install(characterContext)
            : SceneInstallationResult.Failed(
                $"Rig installer '{SceneInstallationMetadata.GetEffectiveInstallerKey(this)}' requires a {nameof(RigInstallationContext)}. "
                + "Run it through a rig role template installer.");
}
