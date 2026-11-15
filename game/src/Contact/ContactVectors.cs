using Godot;

namespace AlleyCat.Contact;

/// <summary>
/// The single Godot ↔ engine-core vector conversion seam (INTR-005): the engine core keeps pure
/// <see cref="System.Numerics.Vector3" /> mathematics below the boundary, and every Godot-side contact component
/// converts through this class so the two vector worlds meet in exactly one place.
/// </summary>
public static class ContactVectors
{
    /// <summary>Converts a Godot world-space vector into the engine core's representation.</summary>
    public static System.Numerics.Vector3 ToNumerics(Vector3 vector) => new(vector.X, vector.Y, vector.Z);

    /// <summary>Converts an engine-core vector into the Godot world-space representation.</summary>
    public static Vector3 ToGodot(System.Numerics.Vector3 vector) => new(vector.X, vector.Y, vector.Z);

    /// <summary>Converts a Godot rotation quaternion into the engine core's representation.</summary>
    public static System.Numerics.Quaternion ToNumerics(Quaternion quaternion)
        => new(quaternion.X, quaternion.Y, quaternion.Z, quaternion.W);
}
