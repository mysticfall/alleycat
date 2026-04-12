using AlleyCat.Common;
using Godot;

namespace AlleyCat.XR.Mock;

/// <summary>
/// Mock XR runtime implementation bound to the mock runtime scene root.
/// </summary>
[GlobalClass]
public partial class MockXRRuntimeNode : Node3D, IXRRuntime, IXROrigin
{
    /// <inheritdoc />
    public IXROrigin Origin => this;

    /// <inheritdoc />
    public IXRCamera Camera
    {
        get;
        private set;
    } = null!;

    /// <inheritdoc />
    public IXRHandController RightHandController
    {
        get;
        private set;
    } = null!;

    /// <inheritdoc />
    public IXRHandController LeftHandController
    {
        get;
        private set;
    } = null!;

    /// <inheritdoc />
    public event Action? PoseRecentered;

    /// <inheritdoc />
    public Node3D OriginNode => this;

    /// <inheritdoc />
    public float WorldScale
    {
        get;
        set;
    } = 1.0f;

    /// <inheritdoc />
    public bool Initialise(SubViewport viewport, int maximumRefreshRate)
    {
        _ = viewport;
        _ = maximumRefreshRate;

        Camera = this.RequireNode<MockXRCameraNode>("MainCamera");
        RightHandController = this.RequireNode<MockXRHandControllerNode>("RightController");
        LeftHandController = this.RequireNode<MockXRHandControllerNode>("LeftController");

        return true;
    }

    /// <summary>
    /// Triggers the <see cref="PoseRecentered"/> event to notify subscribers that the pose has been recentered.
    /// </summary>
    public void TriggerPoseRecentered() => PoseRecentered?.Invoke();
}
