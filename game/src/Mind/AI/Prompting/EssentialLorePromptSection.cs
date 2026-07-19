using AlleyCat.Mind.AI.Lore;
using Godot;
using Microsoft.Extensions.DependencyInjection;

namespace AlleyCat.Mind.AI.Prompting;

/// <summary>
/// Runtime-backed prompt section that injects the observer-specific lore batch for the active content context.
/// </summary>
[GlobalClass]
public partial class EssentialLorePromptSection : PromptSection
{
    /// <inheritdoc />
    public override async Task<string> GetContentAsync(
        PromptSectionBuildContext buildContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(buildContext);

        LoreQuery query;
        try
        {
            query = LoreQuery.Essential(buildContext.Character.Id);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                "EssentialLorePromptSection requires a non-empty, valid observer ID.",
                exception);
        }

        ILoreQueryService queryService = buildContext.Services.GetRequiredService<ILoreQueryService>();
        ILorePromptFormatter formatter = buildContext.Services.GetRequiredService<ILorePromptFormatter>();
        IReadOnlyList<LoreEntry> entries = await queryService.QueryAsync(
            buildContext.Scene.Content,
            query,
            cancellationToken);

        return formatter.Format(entries);
    }
}
