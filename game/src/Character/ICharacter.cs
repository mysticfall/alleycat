using AlleyCat.Body.Eyes;
using AlleyCat.Body.Hands;
using AlleyCat.Body.Voice;
using AlleyCat.Common;
using AlleyCat.Context;
using AlleyCat.Control.Locomotion;
using AlleyCat.Navigation;

namespace AlleyCat.Character;

/// <summary>
/// Aggregate trait for the game's fully embodied humanoid character composition.
/// </summary>
public interface ICharacter : IEntity, IHasHands, IEyesHolder, IHasVoice, ILocomotive, INavigator, IContextual
{
}
