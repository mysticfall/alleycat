using AlleyCat.IntegrationTests.Navigation;
using Godot;

namespace AlleyCat.IntegrationTests.IK;

/// <summary>Earlier signal-boundary evaluation used only by simulated-clock regression tests.</summary>
internal sealed class FootPlantingClockPhysicsReplay : IDisposable
{
    private readonly NPCNavigationRigFixture _fixture;
    private readonly Action _move;
    private readonly bool _wasProcessing;
    private Exception? _failure;

    public FootPlantingClockPhysicsReplay(NPCNavigationRigFixture fixture, Action move)
    {
        _fixture = fixture;
        _move = move;
        _wasProcessing = fixture.Navigation.IsPhysicsProcessing();
        fixture.Navigation.SetPhysicsProcess(false);
        fixture.Actor.GetTree().PhysicsFrame += AdvanceAndMove;
    }

    private void AdvanceAndMove()
    {
        if (_failure is not null)
        {
            return;
        }
        try
        {
            _fixture.Driver.Advance(_fixture.Actor.GetPhysicsProcessDeltaTime());
            _move();
        }
        catch (Exception exception)
        {
            _failure = exception;
        }
    }

    public async Task StepAsync()
    {
        await _fixture.StepAsync(1);
        if (_failure is not null)
        {
            throw new InvalidOperationException("MEASUREMENT: physics replay failed.", _failure);
        }
        _ = await _fixture.Skeleton.ToSignal(_fixture.Skeleton, Skeleton3D.SignalName.SkeletonUpdated);
    }

    public void Dispose()
    {
        _fixture.Actor.GetTree().PhysicsFrame -= AdvanceAndMove;
        _fixture.Navigation.SetPhysicsProcess(_wasProcessing);
    }
}
