using AlleyCat.Control;
using Godot;
using LanguageExt;

namespace AlleyCat.Xr;

public class XrControllerInput2d(
    InputEventName eventName,
    XRController3D controller,
    IObservable<Duration> onProcess
) : Input2d(onProcess)
{
    protected override Vector2 Process(Duration timeDelta) => controller.GetVector2(eventName);
}