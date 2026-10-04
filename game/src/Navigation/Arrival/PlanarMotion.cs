using Godot;

namespace AlleyCat.Navigation.Arrival;

/// <summary>
/// A rigid planar actor motion: an offset in the actor-semantic frame at the start of the motion (<c>X</c> right,
/// <c>Y</c> forward, metres) and a yaw change (radians, positive turns left). Motions compose in playback order with
/// <see cref="Then"/>; double precision keeps long compositions exact to well below a millimetre.
/// </summary>
/// <param name="X">Rightward offset in the start frame, in metres.</param>
/// <param name="Y">Forward offset in the start frame, in metres.</param>
/// <param name="Yaw">Yaw change, in radians (positive turns left).</param>
internal readonly record struct PlanarMotion(double X, double Y, double Yaw)
{
    /// <summary>No motion.</summary>
    public static PlanarMotion Identity => default;

    /// <summary>Length of the offset, in metres.</summary>
    public double Distance => Math.Sqrt((X * X) + (Y * Y));

    /// <summary>
    /// One tick of the runtime movement operation: the actor turns by <paramref name="turn"/> first, then moves by the
    /// root delta <c>(localX, localY)</c> in its turned frame.
    /// </summary>
    public static PlanarMotion Tick(double localX, double localY, double turn)
    {
        (double sin, double cos) = Math.SinCos(turn);
        return new PlanarMotion((cos * localX) - (sin * localY), (sin * localX) + (cos * localY), turn);
    }

    /// <summary>
    /// A root displacement expressed in a root frame of yaw <paramref name="frameYaw"/>, with the yaw change
    /// <paramref name="yawChange"/>.
    /// </summary>
    public static PlanarMotion InFrame(Vector2 displacement, double frameYaw, double yawChange)
    {
        (double sin, double cos) = Math.SinCos(-frameYaw);
        return new PlanarMotion((cos * displacement.X) - (sin * displacement.Y), (sin * displacement.X) + (cos * displacement.Y), yawChange);
    }

    /// <summary>This motion followed by <paramref name="next"/>, whose offset is in the frame this motion ends in.</summary>
    public PlanarMotion Then(PlanarMotion next)
    {
        (double sin, double cos) = Math.SinCos(Yaw);
        return new PlanarMotion(X + (cos * next.X) - (sin * next.Y), Y + (sin * next.X) + (cos * next.Y), Yaw + next.Yaw);
    }

    /// <summary>This motion repeated <paramref name="count"/> times.</summary>
    public PlanarMotion Power(int count)
    {
        PlanarMotion result = Identity;
        for (int index = 0; index < count; index++)
        {
            result = result.Then(this);
        }

        return result;
    }

    /// <summary>The pose this motion reaches from <paramref name="start"/>.</summary>
    public SequencePose From(SequencePose start)
    {
        Vector2 world = SequencePose.SemanticToWorld(new Vector2((float)X, (float)Y), start.Yaw);
        return new SequencePose(start.Position + world, (float)(start.Yaw + Yaw));
    }

    /// <summary>
    /// The motion from <paramref name="start"/> to <paramref name="end"/>, in the start's actor-semantic frame (the
    /// inverse of <see cref="From"/>).
    /// </summary>
    public static PlanarMotion Between(SequencePose start, SequencePose end)
    {
        // SemanticToWorld is its own inverse for a given yaw.
        Vector2 local = SequencePose.SemanticToWorld(end.Position - start.Position, start.Yaw);
        return new PlanarMotion(local.X, local.Y, Mathf.Wrap(end.Yaw - start.Yaw, -Mathf.Pi, Mathf.Pi));
    }
}
