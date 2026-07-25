using Godot;

namespace AlleyCat.Mind.AI;

/// <summary>Requests work after a configured number of successful foreground turns.</summary>
[GlobalClass]
public partial class TurnCountContextWorkerTrigger : ContextWorkerTrigger
{
    private int _settledTurns;

    /// <summary>Number of successful foreground settlements between requests.</summary>
    [Export(PropertyHint.Range, "1,1000,1")]
    public int EverySettledForegroundTurns { get; set; } = 1;

    /// <inheritdoc />
    public override void _Ready()
    {
        if (EverySettledForegroundTurns < 1)
        {
            throw new InvalidOperationException("Turn-count trigger requires a positive turn count.");
        }

        RequireOwningMind().ForegroundTurnSucceeded += OnForegroundTurnSucceeded;
    }

    /// <inheritdoc />
    public override void _ExitTree() => RequireOwningMind().ForegroundTurnSucceeded -= OnForegroundTurnSucceeded;

    private void OnForegroundTurnSucceeded()
    {
        if (++_settledTurns >= EverySettledForegroundTurns)
        {
            _settledTurns = 0;
            RequestRun();
        }
    }
}
