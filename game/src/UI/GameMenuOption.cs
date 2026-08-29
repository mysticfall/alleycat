namespace AlleyCat.UI;

/// <summary>
/// Selectable options presented by the in-game menu.
/// </summary>
public enum GameMenuOption
{
    /// <summary>
    /// Closes the menu and returns to gameplay.
    /// </summary>
    Resume = 0,

    /// <summary>
    /// Requests a clean game exit through <see cref="Game.RequestExit" />.
    /// </summary>
    ExitGame = 1,
}
