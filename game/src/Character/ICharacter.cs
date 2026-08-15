using AlleyCat.Context;
using AlleyCat.Control.Locomotion;
using AlleyCat.Core;
using AlleyCat.Interaction.Hands;
using AlleyCat.Navigation;
using AlleyCat.Speech;
using AlleyCat.Speech.Voice;
using AlleyCat.Vision;

namespace AlleyCat.Character;

/// <summary>
/// Aggregate trait for the game's fully embodied humanoid character composition.
/// </summary>
public interface ICharacter : IContextual, IHasHands, IHasVoice, IHasHearing, ILocomotive, INavigator, IHasVision, IVisualSubject
{
    /// <inheritdoc />
    string IIdentifiable.Type => "char";
}
