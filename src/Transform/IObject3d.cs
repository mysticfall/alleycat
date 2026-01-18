using Godot;
using LanguageExt;

namespace AlleyCat.Transform;

public interface IObject3d
{
    IO<Transform3D> GlobalTransform { get; }
}

public static class Object3dExtensions
{
    extension(IObject3d obj)
    {
        public IO<Vector3> Origin => obj.GlobalTransform.Map(x => x.Origin);

        public IO<Basis> Basis => obj.GlobalTransform.Map(x => x.Basis);
    }
}