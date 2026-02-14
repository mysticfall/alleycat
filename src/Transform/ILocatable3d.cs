using Godot;
using LanguageExt;

namespace AlleyCat.Transform;

public interface ILocatable3d
{
    IO<Transform3D> GlobalTransform { get; }
}

public static class Locatable3dExtensions
{
    extension(ILocatable3d obj)
    {
        public IO<Vector3> Origin => obj.GlobalTransform.Map(x => x.Origin);

        public IO<Basis> Basis => obj.GlobalTransform.Map(x => x.Basis);
    }
}