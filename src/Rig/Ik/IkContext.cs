using Godot;

namespace AlleyCat.Rig.Ik;

public interface IIkContext
{
    Transform3D ToSkeleton { get; }

    Transform3D FromSkeleton { get; }
}