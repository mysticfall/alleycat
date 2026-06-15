using AlleyCat.Core.Installer;
using Godot;

namespace AlleyCat.Character.Installer;

/// <summary>
/// Base class for character-specific installers that resolve shared character dependencies from CORE-005 context.
/// </summary>
[GlobalClass]
public abstract partial class CharacterSceneInstaller : SceneInstaller, ISceneInstaller<CharacterInstallationContext>
{
    /// <summary>
    /// Installs this character module using explicit template and skeleton dependencies.
    /// </summary>
    public abstract SceneInstallationResult Install(CharacterInstallationContext context);

    /// <inheritdoc />
    public override SceneInstallationResult Install(SceneInstallationContext context)
        => context is CharacterInstallationContext characterContext
            ? Install(characterContext)
            : SceneInstallationResult.Failed(
                $"Character installer '{SceneInstallationMetadata.GetEffectiveInstallerKey(this)}' requires a {nameof(CharacterInstallationContext)}. "
                + "Run it through a character role template installer.");
}
