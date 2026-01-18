using Godot;

namespace AlleyCat.Rig;

[Tool]
public partial class RagDoll : PhysicalBoneSimulator3D
{
    public override void _Ready()
    {
        base._Ready();

        PhysicalBonesStartSimulation(["breast_r", "breast_l"]);
    }
}