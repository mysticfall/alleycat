using AlleyCat.XR;
using Godot;

namespace AlleyCat.IK;

/// <summary>
/// IK target provider that follows the XR camera as the player head target.
/// </summary>
[GlobalClass]
public partial class XRHeadTargetProvider : IKTargetStateProvider
{
    private IXRCamera? _camera;
    private Transform3D _viewpointLocalInverseTransform = Transform3D.Identity;

    /// <summary>
    /// Avatar viewpoint marker used to derive the head IK target from the XR camera pose.
    /// </summary>
    [Export]
    public Marker3D? Viewpoint
    {
        get; set;
    }

    private bool TryResolveCamera()
    {
        Camera3D? cameraNode = _camera?.CameraNode;
        if (cameraNode is not null && IsInstanceValid(cameraNode))
        {
            return true;
        }

        Marker3D? viewpoint = Viewpoint;
        if (viewpoint is null || !IsInstanceValid(viewpoint))
        {
            _camera = null;
            _viewpointLocalInverseTransform = Transform3D.Identity;
            return false;
        }

        IXRRuntime? runtime = ResolveXRRuntime();
        if (runtime is null)
        {
            _camera = null;
            _viewpointLocalInverseTransform = Transform3D.Identity;
            return false;
        }

        _camera = runtime.Camera;
        _viewpointLocalInverseTransform = viewpoint.Transform.Inverse();
        return true;
    }

    /// <inheritdoc />
    public override IKTargetState GetTargetState()
    {
        _ = TryResolveCamera();
        Camera3D? cameraNode = _camera?.CameraNode;
        return cameraNode is not null && IsInstanceValid(cameraNode)
            ? new IKTargetState(cameraNode.GlobalTransform * _viewpointLocalInverseTransform, 1.0f)
            : new IKTargetState(Transform3D.Identity, 0.0f);
    }

    private static IXRRuntime? ResolveXRRuntime()
    {
        try
        {
            XRManager? xrManager = Game.Instance.GetService<XRManager>();
            return xrManager?.Runtime;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
