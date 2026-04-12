using Godot;

namespace AlleyCat.XR.Mock;

/// <summary>
/// Mock XR camera component implementing XR camera abstraction.
/// </summary>
[GlobalClass]
public partial class MockXRCameraNode : Camera3D, IXRCamera
{
    /// <inheritdoc />
    public Camera3D CameraNode => this;
}
