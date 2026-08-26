using AlleyCat.Control.Locomotion;
using AlleyCat.Core;
using AlleyCat.Interaction.Hands;
using AlleyCat.Navigation;
using AlleyCat.Speech;
using AlleyCat.Speech.Voice;
using AlleyCat.Templating;
using AlleyCat.Vision;

namespace AlleyCat.Character;

/// <summary>
/// Aggregate trait for the game's fully embodied humanoid character composition. Sealed for template rendering:
/// a character's entire template surface is exactly the curated members it inherits, such as
/// <see cref="IIdentifiable.FullId" />, and every other member access renders nil so live component state stays
/// out of authored prompts.
/// </summary>
[TemplateSealed]
public interface ICharacter : IHasHands, IHasVoice, IHasHearing, ILocomotive, INavigator, IHasVision, IVisualSubject
{
    /// <inheritdoc />
    string IIdentifiable.Type => "char";
}
