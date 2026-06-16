using AlleyCat.Core.Installer;
using AlleyCat.Rigging.Installation;
using Godot;

namespace AlleyCat.Rigging.Physics;

/// <summary>
/// Installs authored body-part attachment points for a humanoid character under the resolved skeleton.
/// </summary>
[Tool]
[GlobalClass]
public partial class BodyPartsInstaller : RigSubsystemInstaller
{
    /// <inheritdoc />
    public override SceneInstallationResult Install(RigInstallationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            RigTemplateInstallation.RebindDirectBoneAttachments(context.Skeleton, this);
            return SceneInstallationResult.Successful();
        }
        catch (InvalidOperationException ex)
        {
            return SceneInstallationResult.Failed(ex.Message);
        }
    }
}
