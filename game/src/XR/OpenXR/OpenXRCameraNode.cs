using Godot;

namespace AlleyCat.XR.OpenXR;

/// <summary>
/// OpenXR camera component implementing XR camera abstraction.
/// </summary>
[GlobalClass]
public partial class OpenXRCameraNode : XRCamera3D, IXRCamera
{
    /// <inheritdoc />
    public Camera3D CameraNode => this;
}
