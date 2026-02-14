using Godot;
using LanguageExt;
using static LanguageExt.Prelude;

namespace AlleyCat.Transform;

public interface IMovable3d : ILocatable3d
{
    IO<Unit> SetGlobalTransform(Transform3D transform);
}

public static class Movable3dExtensions
{
    extension(IMovable3d obj)
    {
        public IO<Unit> MoveTo(Vector3 position) =>
            from transform in obj.GlobalTransform
            from _ in obj.SetGlobalTransform(new Transform3D(transform.Basis, position))
            select unit;

        public IO<Unit> MoveBy(Vector3 offset) =>
            from transform in obj.GlobalTransform
            from _ in obj.SetGlobalTransform(transform.Translated(offset))
            select unit;
    }
}