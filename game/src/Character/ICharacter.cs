using AlleyCat.Body.Eyes;
using AlleyCat.Body.Hands;
using AlleyCat.Body.Voice;
using AlleyCat.Context;
using AlleyCat.Control.Locomotion;
using AlleyCat.Core;
using AlleyCat.Navigation;

namespace AlleyCat.Character;

/// <summary>
/// Aggregate trait for the game's fully embodied humanoid character composition.
/// </summary>
public interface ICharacter : IIdentifiable, IContextual, IHasHands, IHasVoice, ILocomotive, INavigator, IEyesHolder, IVisualSubject
{
    /// <inheritdoc />
    string IIdentifiable.Type => "char";
}
