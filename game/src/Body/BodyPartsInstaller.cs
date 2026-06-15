using AlleyCat.Character.Installer;
using AlleyCat.Core.Installer;
using Godot;

namespace AlleyCat.Body;

/// <summary>
/// Installs authored body-part attachment points for a humanoid character under the resolved skeleton.
/// </summary>
[Tool]
[GlobalClass]
public partial class BodyPartsInstaller : CharacterSubsystemInstaller
{
    /// <inheritdoc />
    public override SceneInstallationResult Install(CharacterInstallationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            CharacterTemplateInstallation.RebindDirectBoneAttachments(context.Skeleton, this);
            return SceneInstallationResult.Successful();
        }
        catch (InvalidOperationException ex)
        {
            return SceneInstallationResult.Failed(ex.Message);
        }
    }
}
