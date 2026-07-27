using AlleyCat.Body.Eyes;
using AlleyCat.Body.Hands;
using AlleyCat.Body.Voice;
using AlleyCat.Control.Locomotion;
using AlleyCat.Core;
using AlleyCat.Navigation;

namespace AlleyCat.Character;

/// <summary>
/// Aggregate trait for the game's fully embodied humanoid character composition.
/// </summary>
public interface ICharacter : IIdentifiable, IHasHands, IHasVoice, ILocomotive, INavigator, IVisualObserver, IVisualSubject
{
    /// <inheritdoc />
    string IIdentifiable.Type => "char";
}
