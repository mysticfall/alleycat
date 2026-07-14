using AlleyCat.Mind.AI.Lore;
using Godot;
using Microsoft.Extensions.DependencyInjection;

namespace AlleyCat.Mind.AI.Prompting;

/// <summary>
/// Runtime-backed prompt section that injects all essential lore for the active content context.
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

        ILoreQueryService queryService = buildContext.Services.GetRequiredService<ILoreQueryService>();
        ILorePromptFormatter formatter = buildContext.Services.GetRequiredService<ILorePromptFormatter>();
        IReadOnlyList<LoreEntry> entries = await queryService.QueryAsync(
            buildContext.Scene.Content,
            LoreQuery.Essential(),
            cancellationToken);

        return formatter.Format(entries);
    }
}
