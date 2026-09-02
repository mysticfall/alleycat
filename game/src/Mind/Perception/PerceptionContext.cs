using AlleyCat.Character;
using AlleyCat.Scene;

namespace AlleyCat.Mind.Perception;

/// <summary>Current synchronous interpretation dependencies supplied by Mind.</summary>
/// <param name="Character">Owning character whose faculties interpret the percept.</param>
/// <param name="Scene">Current scene context used for attribution resolution.</param>
public sealed record PerceptionContext(ICharacter Character, ISceneContext Scene);
