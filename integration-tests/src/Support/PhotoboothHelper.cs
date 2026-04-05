using Godot;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Support;

/// <summary>
/// Static helper for capturing screenshots from Photobooth scenes loaded in
/// C# integration tests. The Photobooth and CameraRig types are GDScript
/// classes, so all method invocations go through <c>GodotObject.Call</c>.
/// </summary>
public static class PhotoboothHelper
{
    /// <summary>
    /// Number of frames to wait after calling the multi-rig
    /// <c>capture_screenshots</c> coroutine (5 rigs × ~3 frames + slack).
    /// </summary>
    private const int CaptureAllFrames = 20;

    /// <summary>
    /// Number of frames to wait after calling the single-rig
    /// <c>capture_screenshot</c> coroutine.
    /// </summary>
    private const int CaptureSingleFrames = 5;

    /// <summary>
    /// Returns <c>false</c> when Godot is running with a headless display server,
    /// which uses a dummy rendering driver that cannot produce viewport textures.
    /// </summary>
    private static bool CanCaptureScreenshots() =>
        !string.Equals(DisplayServer.GetName(), "headless", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Captures screenshots from all visible camera rigs in the photobooth by invoking
    /// the GDScript <c>capture_screenshots</c> coroutine and waiting for it to settle.
    /// </summary>
    public static async Task CaptureScreenshotsAsync(
        SceneTree sceneTree, GodotObject photobooth, string fileName)
    {
        if (!CanCaptureScreenshots())
        {
            return;
        }

        _ = photobooth.Call("capture_screenshots", fileName);
        await WaitForFramesAsync(sceneTree, CaptureAllFrames);
    }

    /// <summary>
    /// Captures a screenshot from a single camera rig by invoking the GDScript
    /// <c>capture_screenshot</c> coroutine and waiting for it to settle.
    /// </summary>
    public static async Task CaptureScreenshotAsync(
        SceneTree sceneTree, GodotObject cameraRig, string fileName)
    {
        if (!CanCaptureScreenshots())
        {
            return;
        }

        _ = cameraRig.Call("capture_screenshot", fileName);
        await WaitForFramesAsync(sceneTree, CaptureSingleFrames);
    }

    /// <summary>
    /// Retrieves a camera rig by name from the photobooth via GDScript interop.
    /// </summary>
    public static GodotObject GetCameraRig(GodotObject photobooth, string cameraName) =>
        (GodotObject)photobooth.Call("get_camera_rig", cameraName);
}
