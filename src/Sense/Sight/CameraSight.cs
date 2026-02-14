using AlleyCat.Transform;
using Godot;
using LanguageExt;
using static LanguageExt.Prelude;

namespace AlleyCat.Sense.Sight;

public readonly record struct CameraVision(
    Image Image,
    DateTime Timestamp
) : IVision;

public interface ICameraSight : ISight
{
    Camera3D Camera { get; }

    IO<Transform3D> ILocatable3d.GlobalTransform => lift(() => Camera.GlobalTransform);

    SourceT<IO, IVision> IActiveSense<IVision>.Perceptions =>
        SourceT.foreverM(CaptureImage().Map<IVision>(i => new CameraVision(i, DateTime.Now)));

    IO<Image> CaptureImage(int? maxSize = null) => liftIO(async () =>
    {
        var server = RenderingServer.Singleton;

        var viewport = (SubViewport)Camera.GetViewport();

        await server.ToSignal(server, RenderingServer.SignalName.FramePreDraw);

        viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;

        await server.ToSignal(server, RenderingServer.SignalName.FramePostDraw);

        var image = viewport.GetTexture().GetImage();

        viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;

        var width = image.GetWidth();
        var height = image.GetHeight();

        if (maxSize != null && (width > maxSize || height > maxSize))
        {
            var ratio = (float)width / height;

            if (width > height)
            {
                width = (int)maxSize;
                height = (int)(width / ratio);
            }
            else
            {
                height = (int)maxSize;
                width = (int)(height * ratio);
            }

            image.Resize(width, height);
        }

        return image;
    });
}