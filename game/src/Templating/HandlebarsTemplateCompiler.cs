using AlleyCat.Core;
using Godot;
using Microsoft.Extensions.DependencyInjection;

namespace AlleyCat.Templating;

/// <summary>
/// Handlebars.Net-backed template compiler with deterministic helper and partial registration.
/// </summary>
[Tool]
[GlobalClass]
public sealed partial class HandlebarsTemplateCompiler : Resource, ITemplateCompiler, IServiceRegistrar
{
    private readonly HandlebarsTemplateCompilerEngine _engine = new();
    private bool _configurationApplied;

    /// <summary>
    /// Directory containing partial templates to register during compiler configuration.
    /// </summary>
    [Export(PropertyHint.Dir)]
    public string PartialDirectoryPath { get; set; } = string.Empty;

    /// <summary>
    /// Programmatic tools registered after built-in tools.
    /// </summary>
    public ITemplateTool[] Tools { get; set; } = [];

    /// <summary>
    /// Godot-authored tool resources registered after programmatic tools.
    /// </summary>
    [Export]
    public Godot.Collections.Array<TemplateToolResource> ToolResources { get; set; } = [];

    /// <summary>
    /// Creates a compiler with built-in tools registered.
    /// </summary>
    public HandlebarsTemplateCompiler()
        : this(null, null)
    {
    }

    /// <summary>
    /// Creates a compiler and registers built-in tools, plus any supplied partials and tools.
    /// </summary>
    /// <param name="partials">Optional partial templates keyed by partial name.</param>
    /// <param name="tools">Optional additional tools to register after built-ins.</param>
    public HandlebarsTemplateCompiler(
        IReadOnlyDictionary<string, string>? partials = null,
        IEnumerable<ITemplateTool>? tools = null)
    {
        if (partials is not null)
        {
            foreach (KeyValuePair<string, string> partial in partials)
            {
                RegisterPartial(partial.Key, partial.Value);
            }
        }

        if (tools is not null)
        {
            foreach (ITemplateTool tool in tools)
            {
                RegisterTool(tool);
            }
        }
    }

    /// <summary>
    /// Applies exported Godot configuration, registering configured partials and tools exactly once.
    /// </summary>
    public void ApplyConfiguration()
    {
        if (_configurationApplied)
        {
            return;
        }

        HandlebarsTemplateCompilerConfiguration.Apply(_engine, PartialDirectoryPath, Tools, ToolResources);
        _configurationApplied = true;
    }

    /// <summary>
    /// Registers a partial template. Names are unique and ordinal case-sensitive.
    /// </summary>
    /// <param name="name">Partial name.</param>
    /// <param name="source">Partial source text.</param>
    public void RegisterPartial(string name, string source) => _engine.RegisterPartial(name, source);

    /// <summary>
    /// Registers a helper/tool. Names are unique and ordinal case-sensitive.
    /// </summary>
    /// <param name="tool">The tool to register.</param>
    public void RegisterTool(ITemplateTool tool) => _engine.RegisterTool(tool);

    /// <inheritdoc />
    public ITemplate Compile(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ApplyConfiguration();

        return _engine.Compile(source);
    }

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection services)
    {
        ApplyConfiguration();
        _ = services.AddSingleton<ITemplateCompiler>(this);
    }

}
