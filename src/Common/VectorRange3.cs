using Godot;

namespace AlleyCat.Common;

[GlobalClass, Tool]
public partial class VectorRange3 : Resource
{
    [Export] public Vector3 Min { get; set; }

    [Export] public Vector3 Max { get; set; }

    public VectorRange3()
    {
    }

    public VectorRange3(Vector3 min, Vector3 max)
    {
        Min = min;
        Max = max;
    }

    public Vector3 Clamp(Vector3 vector) => vector.Clamp(Min, Max);
}