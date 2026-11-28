using AlleyCat.Animations;
using AlleyCat.Core.Threading;
using Godot;
using Microsoft.Extensions.DependencyInjection;

namespace AlleyCat.Mind.AI.Prompting;

/// <summary>
/// Session-start prompt section listing the owning character's compatible stances and gaits by backticked id and tags
/// (ANIM-003 Mind Integration, Prompt Catalogue Fragment). It is rendered once with the system instruction and never
/// rebuilt. A character without an animation catalogue contributes empty content.
/// </summary>
[GlobalClass]
public partial class AnimationCataloguePromptSection : PromptSection
{
    /// <summary>In-character heading above the idle (stance) entries.</summary>
    [Export(PropertyHint.MultilineText)]
    public string StanceHeading { get; set; } = "Stances you can take up with `change_stance`:";

    /// <summary>In-character heading above the locomotion (gait) entries.</summary>
    [Export(PropertyHint.MultilineText)]
    public string GaitHeading { get; set; } = "Gaits you can move with using `change_gait`:";

    /// <summary>Catalogue ids and tags are authored data, never template source.</summary>
    public override bool IsTemplateOpaque => true;

    /// <inheritdoc />
    public override async Task<string> GetContentAsync(
        PromptSectionBuildContext buildContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(buildContext);
        cancellationToken.ThrowIfCancellationRequested();
        if (buildContext.Character is not Character.Character character)
        {
            return string.Empty;
        }

        string content = string.Empty;
        IMainThreadDispatcher? dispatcher = buildContext.Services.GetService<IMainThreadDispatcher>();
        if (dispatcher is null)
        {
            return await ReadAsync(character, cancellationToken);
        }

        await dispatcher.InvokeAsync(
            async token => content = await ReadAsync(character, token),
            cancellationToken);
        return content;
    }

    private async Task<string> ReadAsync(Character.Character character, CancellationToken cancellationToken)
    {
        AnimationCatalogue[] catalogues = [.. character.GetChildren().OfType<AnimationCatalogue>()];
        if (catalogues.Length != 1)
        {
            return string.Empty;
        }

        AnimationCatalogue catalogue = catalogues[0];
        if (!catalogue.IsBuilt && character.IsInsideTree())
        {
            // The character's navigation builds the catalogue in its deferred initialisation, after packs added during
            // scene setup; a session that starts in _Ready waits one frame instead of building it early.
            _ = await character.ToSignal(character.GetTree(), SceneTree.SignalName.ProcessFrame);
            cancellationToken.ThrowIfCancellationRequested();
        }

        return AnimationCatalogueFragment.Format(
            catalogue.Idle.Entries,
            catalogue.Locomotion.Entries,
            StanceHeading,
            GaitHeading);
    }
}
