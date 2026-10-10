using Godot;

namespace AlleyCat.Testing;

/// <summary>Test-only engine callback recorder, compiled with Godot's native callback binding generator.</summary>
public partial class FootPlantingPhysicsDeltaObserver : Node
{
    /// <summary>Observed engine callback arguments, independent of the planting clock.</summary>
    public Dictionary<ulong, double> Deltas { get; } = [];

    /// <summary>Records before the production planting observer.</summary>
    public FootPlantingPhysicsDeltaObserver()
    {
        ProcessPhysicsPriority = -102;
    }

    /// <inheritdoc />
    public override void _PhysicsProcess(double delta) => Deltas.Add(Engine.GetPhysicsFrames(), delta);
}
