using AlleyCat.Entity;
using AlleyCat.Env;
using AlleyCat.Template;
using LanguageExt;
using static LanguageExt.Prelude;
using Vector2 = Godot.Vector2;

namespace AlleyCat.Tests.Sense.Sight;

public class MockGazeContextProvider : ITemplateContextProvider
{
    public Vector2 PitchYaw { get; set; }

    public bool LookingAtTheFace { get; set; }

    private const float MaxForwardRotation = 20f;

    public Eff<IEnv, Map<object, object?>> CreateContext(ITemplateRenderable subject,
        IEntity observer) => liftEff(() =>
    {
        var r = PitchYaw;

        return Map<object, object?>(
            ("gaze", Map<object, object?>(
                ("looking_at_the_face", LookingAtTheFace),
                ("yaw", r.X),
                ("pitch", r.Y),
                (
                    "forward",
                    Math.Abs(r.X) <= MaxForwardRotation && Math.Abs(r.Y) <= MaxForwardRotation
                ),
                (
                    "up",
                    Math.Abs(r.X) <= MaxForwardRotation && r.Y > MaxForwardRotation
                ),
                (
                    "down",
                    Math.Abs(r.X) <= MaxForwardRotation && r.Y < -MaxForwardRotation
                ),
                (
                    "right",
                    r.X > MaxForwardRotation && Math.Abs(r.Y) <= MaxForwardRotation
                ),
                (
                    "left",
                    r.X < -MaxForwardRotation && Math.Abs(r.Y) <= MaxForwardRotation
                ),
                (
                    "up_right",
                    r is { X: > MaxForwardRotation, Y: > MaxForwardRotation }
                ),
                (
                    "up_left",
                    r is { X: < -MaxForwardRotation, Y: > MaxForwardRotation }
                ),
                (
                    "down_right",
                    r is { X: > MaxForwardRotation, Y: < -MaxForwardRotation }
                ),
                (
                    "down_left",
                    r is { X: < -MaxForwardRotation, Y: < -MaxForwardRotation }
                )
            ))
        );
    });
}