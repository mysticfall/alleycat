using System.Text;
using AlleyCat.Common;
using AlleyCat.Core;
using AlleyCat.Templating;
using Godot;
using Microsoft.Extensions.DependencyInjection;

namespace AlleyCat.Mind.AI.Prompting;

/// <summary>
/// Writes prompt sections as a simple pseudo-XML text block.
/// </summary>
[Tool]
[GlobalClass]
public sealed partial class PseudoXmlPromptWriter : Resource, IPromptWriter, IServiceRegistrar
{
    /// <inheritdoc />
    public Task<string> WriteAsync(
        IReadOnlyCollection<PromptSection> sections,
        PromptSectionBuildContext buildContext,
        CancellationToken cancellationToken = default)
        => FormatAsync(sections, buildContext, cancellationToken);

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection services)
        => services.AddSingleton<IPromptWriter>(this);

    internal static async Task<string> FormatAsync(
        IReadOnlyCollection<PromptSection> sections,
        PromptSectionBuildContext buildContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentNullException.ThrowIfNull(buildContext);

        if (sections.Count == 0)
        {
            return string.Empty;
        }

        StringBuilder builder = new();
        foreach (PromptSection? section in sections)
        {
            if (section is null)
            {
                throw new InvalidOperationException("Prompt writer cannot serialise a null section entry.");
            }

            await AppendSectionAsync(builder, section, buildContext, cancellationToken);
        }

        return builder.ToString();
    }

    private static async Task AppendSectionAsync(
        StringBuilder builder,
        PromptSection section,
        PromptSectionBuildContext buildContext,
        CancellationToken cancellationToken)
    {
        string content = await section.GetContentAsync(buildContext, cancellationToken) ?? string.Empty;

        // Opaque sections contribute authored data, not template source: their content is escaped as it enters
        // the assembled source so the single session-start compile emits it verbatim.
        if (section.IsTemplateOpaque)
        {
            content = FluidTemplateEscaping.EscapeOpeners(content);
        }

        PseudoXmlFormatter.AppendBlock(builder, section.Name, content, "Prompt sections");
    }
}
