namespace AlleyCat.Core.Time;

/// <summary>
/// Provides the game's notion of time as seconds elapsed since the game began.
/// </summary>
public interface IGameClock
{
    /// <summary>
    /// Gets the monotonic, non-negative number of seconds elapsed since the game began.
    /// In-game time currently advances one second per second of real time.
    /// </summary>
    double NowSeconds
    {
        get;
    }
}
