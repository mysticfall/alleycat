using Godot;

namespace AlleyCat.Core;

/// <summary>
/// Spatial capability trait for objects that occupy a position and orientation in world space. The transform is
/// read-only through this contract; implementers own mutation.
/// </summary>
public interface ISpatial
{
    /// <summary>Gets the object's transform in world space.</summary>
    Transform3D GlobalTransform
    {
        get;
    }
}
