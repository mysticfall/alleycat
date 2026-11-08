using Godot;

namespace AlleyCat.Vision;

/// <summary>
/// Serialised one-shot viewport image capture for a character's vision camera. Enforces the
/// VISION-001 capture lifecycle: per-capture camera re-sync, the prototype-validated one-shot
/// sequence for non-continuous viewports, frame-awaiting readback for continuous viewports,
/// overlap serialisation, and teardown invalidation without hangs or late writes.
/// </summary>
internal sealed class VisionImageCapture
{
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly Dictionary<Camera3D, Transform3D> _authoredCameraOffsets = [];
    private long _generation;

    /// <summary>
    /// Invalidates all in-flight and queued captures and forgets cached camera authoring offsets.
    /// Invoked on component teardown and whenever the configured camera or eye origin changes.
    /// Invalidated captures fail clearly instead of writing to freed objects; queued waiters fault
    /// at their next checkpoint rather than hanging.
    /// </summary>
    public void Invalidate()
    {
        _generation++;
        _authoredCameraOffsets.Clear();
    }

    /// <summary>
    /// Captures exactly one fresh frame from <paramref name="camera"/>'s viewport.
    /// </summary>
    /// <param name="camera">The vision camera whose viewport is read back.</param>
    /// <param name="eyeOrigin">
    /// The eye-origin anchor the camera is re-synced from when
    /// <paramref name="resyncCameraFromEyeOrigin"/> is set.
    /// </param>
    /// <param name="resyncCameraFromEyeOrigin">
    /// Whether the capture path owns the camera's world transform. Template-authored rig cameras
    /// under a head-mounted <see cref="SubViewport"/> do not inherit the head transform chain and
    /// must be re-synced from the eye origin on every capture; runtime-resolved cameras (the
    /// player's XR camera) are never moved by the capture path.
    /// </param>
    /// <returns>The readback image of its own render frame.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the camera or viewport is invalid or freed, when a teardown or reconfiguration
    /// invalidated the capture, or when the readback is empty.
    /// </exception>
    public async ValueTask<Image> CaptureAsync(
        Camera3D camera,
        Node3D? eyeOrigin,
        bool resyncCameraFromEyeOrigin)
    {
        long generation = _generation;
        await _captureGate.WaitAsync();
        try
        {
            EnsureOperable(generation, camera);
            SubViewport viewport = ResolveViewport(camera);
            if (resyncCameraFromEyeOrigin)
            {
                ResyncCameraFromEyeOrigin(camera, eyeOrigin);
            }

            bool continuouslyUpdating = viewport.RenderTargetUpdateMode
                is SubViewport.UpdateMode.Always
                or SubViewport.UpdateMode.WhenVisible
                or SubViewport.UpdateMode.WhenParentVisible;
            if (continuouslyUpdating)
            {
                // Continuous viewport (the player's live XR viewport): never mutate its render-target
                // settings; await the next completed frame and read back the viewport texture.
                await AwaitFrameSignalAsync(RenderingServer.SignalName.FramePreDraw);
                EnsureOperable(generation, camera, viewport);
                await AwaitFrameSignalAsync(RenderingServer.SignalName.FramePostDraw);
                EnsureOperable(generation, camera, viewport);
                return ReadImage(viewport);
            }

            // Non-continuous viewport (the NPC rig at rest): the prototype-validated one-shot
            // sequence, restoring the prior update mode after readback.
            SubViewport.UpdateMode previousUpdateMode = viewport.RenderTargetUpdateMode;
            await AwaitFrameSignalAsync(RenderingServer.SignalName.FramePreDraw);
            EnsureOperable(generation, camera, viewport);
            viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
            await AwaitFrameSignalAsync(RenderingServer.SignalName.FramePostDraw);
            EnsureOperable(generation, camera, viewport);
            Image image = ReadImage(viewport);
            RestoreUpdateMode(viewport, previousUpdateMode);
            return image;
        }
        finally
        {
            _ = _captureGate.Release();
        }
    }

    private static async Task AwaitFrameSignalAsync(StringName signal)
        => await RenderingServer.Singleton.ToSignal(RenderingServer.Singleton, signal);

    private static SubViewport ResolveViewport(Camera3D camera)
        => camera.GetViewport() as SubViewport
            ?? throw new InvalidOperationException(
                $"Vision capture requires the vision camera '{camera.GetPath()}' to render into a {nameof(SubViewport)}, "
                + $"but its viewport is '{camera.GetViewport()?.GetClass() ?? "null"}'.");

    private void ResyncCameraFromEyeOrigin(Camera3D camera, Node3D? eyeOrigin)
    {
        if (eyeOrigin is null || !GodotObject.IsInstanceValid(eyeOrigin))
        {
            throw new InvalidOperationException(
                $"Vision capture requires a valid {nameof(EyesBehaviour.EyeOrigin)} anchor to re-sync the vision camera '{camera.GetPath()}'.");
        }

        // The authored local transform is the offset (its rotation is the pitch-offset mechanism);
        // a Camera3D under a head-mounted SubViewport does not inherit the head's 3D transform
        // chain, so without this re-sync it silently renders from the world origin. The offset is
        // captured from the pristine authored transform on first use because writing the composed
        // world transform back through the viewport-rooted camera overwrites its local transform.
        if (!_authoredCameraOffsets.TryGetValue(camera, out Transform3D authoredLocalOffset))
        {
            authoredLocalOffset = camera.Transform;
            _authoredCameraOffsets[camera] = authoredLocalOffset;
        }

        camera.GlobalTransform = eyeOrigin.GlobalTransform * authoredLocalOffset;
    }

    private void EnsureOperable(long generation, Camera3D camera, SubViewport? viewport = null)
    {
        if (generation != _generation)
        {
            throw new InvalidOperationException(
                "Vision capture was invalidated by component teardown or reconfiguration before its frame completed.");
        }

        if (!GodotObject.IsInstanceValid(camera))
        {
            throw new InvalidOperationException(
                "Vision capture requires a valid vision camera, but the configured camera was freed mid-capture.");
        }

        if (viewport is not null && !GodotObject.IsInstanceValid(viewport))
        {
            throw new InvalidOperationException(
                "Vision capture requires a valid viewport, but the vision camera's viewport was freed mid-capture.");
        }
    }

    private static Image ReadImage(SubViewport viewport)
    {
        Image image = viewport.GetTexture().GetImage();
        return image.IsEmpty()
            ? throw new InvalidOperationException(
                "Vision capture read back an empty image; the vision viewport produced no render.")
            : image;
    }

    private static void RestoreUpdateMode(SubViewport viewport, SubViewport.UpdateMode previousUpdateMode)
    {
        if (GodotObject.IsInstanceValid(viewport))
        {
            viewport.RenderTargetUpdateMode = previousUpdateMode;
        }
    }
}
