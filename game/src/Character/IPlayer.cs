using AlleyCat.Control.Locomotion;

namespace AlleyCat.Character;

/// <summary>
/// Aggregate trait for a player character with player-owned locomotion.
/// </summary>
public interface IPlayer : ICharacter, ILocomotive
{
}
