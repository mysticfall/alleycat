using HandlebarsDotNet;

namespace AlleyCat.Templating;

/// <summary>
/// Plain .NET Handlebars compiler engine used by the Godot-authored compiler resource.
/// </summary>
internal sealed class HandlebarsTemplateCompilerEngine : ITemplateCompiler
{
    private readonly IHandlebars _handlebars = Handlebars.Create();
    private readonly HashSet<string> _registeredPartials = new(StringComparer.Ordinal);
    private readonly HashSet<string> _registeredTools = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates an engine with built-in tools registered.
    /// </summary>
    public HandlebarsTemplateCompilerEngine()
    {
        foreach (ITemplateTool tool in BuiltInTemplateTools.All)
        {
            RegisterTool(tool);
        }
    }

    /// <summary>
    /// Registers a partial template. Names are unique and ordinal case-sensitive.
    /// </summary>
    /// <param name="name">Partial name.</param>
    /// <param name="source">Partial source text.</param>
    public void RegisterPartial(string name, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(source);

        if (!_registeredPartials.Add(name))
        {
            throw new InvalidOperationException($"Template partial '{name}' is already registered.");
        }

        _handlebars.RegisterTemplate(name, source);
    }

    /// <summary>
    /// Registers a helper/tool. Names are unique and ordinal case-sensitive.
    /// </summary>
    /// <param name="tool">The tool to register.</param>
    public void RegisterTool(ITemplateTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentException.ThrowIfNullOrWhiteSpace(tool.Name);

        if (!_registeredTools.Add(tool.Name))
        {
            throw new InvalidOperationException($"Template tool '{tool.Name}' is already registered.");
        }

        _handlebars.RegisterHelper(tool.Name, (_, arguments) => tool.Render([.. arguments]));
    }

    /// <inheritdoc />
    public ITemplate Compile(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        HandlebarsTemplate<object, object> template = _handlebars.Compile(source);

        return new CompiledHandlebarsTemplate(template);
    }

    private sealed class CompiledHandlebarsTemplate(HandlebarsTemplate<object, object> template) : ITemplate
    {
        public string Render(IReadOnlyDictionary<string, object?> context) => template(context);
    }
}
