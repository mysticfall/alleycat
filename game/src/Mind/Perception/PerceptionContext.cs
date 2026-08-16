using AlleyCat.Character;
using AlleyCat.Mind.Attention;
using AlleyCat.Scene;

namespace AlleyCat.Mind.Perception;

/// <summary>Current synchronous interpretation dependencies supplied by Mind.</summary>
public sealed record PerceptionContext(ICharacter Character, ISceneContext Scene, AttentionSettings AttentionSettings);
