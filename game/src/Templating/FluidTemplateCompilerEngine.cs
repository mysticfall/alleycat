using System.Text;
using Fluid;
using Fluid.Values;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace AlleyCat.Templating;

/// <summary>
/// Plain .NET Liquid (Fluid) compiler engine used by the Godot-authored compiler resource.
/// </summary>
internal sealed class FluidTemplateCompilerEngine : ITemplateCompiler
{
    private static readonly FluidParser _sharedParser = new(new FluidParserOptions { AllowFunctions = true });

    private readonly HashSet<string> _registeredPartials = new(StringComparer.Ordinal);
    private readonly HashSet<string> _registeredTools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _partialSources = new(StringComparer.Ordinal);
    private volatile ITemplateTool[] _tools = [.. BuiltInTemplateTools.All];
    private readonly TemplateOptions _templateOptions;

    /// <summary>
    /// Creates an engine with built-in tools registered.
    /// </summary>
    public FluidTemplateCompilerEngine()
    {
        foreach (ITemplateTool tool in _tools)
        {
            _ = _registeredTools.Add(tool.Name);
        }

        _templateOptions = new TemplateOptions
        {
            MemberAccessStrategy = UnsafeMemberAccessStrategy.Instance,
            ModelNamesComparer = StringComparer.Ordinal,
            StrictVariables = false,
            FileProvider = new RegisteredPartialFileProvider(_partialSources),
        };
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

        _partialSources[name] = source;
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

        ITemplateTool[] currentTools = _tools;
        ITemplateTool[] updatedTools = [.. currentTools, tool];
        _tools = updatedTools;
    }

    /// <inheritdoc />
    public ITemplate Compile(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        // Throws Fluid.ParseException for malformed Liquid sources such as unclosed blocks.
        IFluidTemplate template = _sharedParser.Parse(source);

        return new CompiledFluidTemplate(this, template);
    }

    private static ValueTask<FluidValue> InvokeToolAsync(ITemplateTool tool, FunctionArguments arguments)
    {
        object?[] convertedArguments = new object?[arguments.Count];
        for (int index = 0; index < convertedArguments.Length; index++)
        {
            convertedArguments[index] = ToToolArgument(arguments.At(index), index);
        }

        string result = tool.Render(convertedArguments);
        return ValueTask.FromResult<FluidValue>(new StringValue(result));
    }

    private static object? ToToolArgument(FluidValue value, int position)
    {
        if (value.IsNil())
        {
            // Unresolved variables become positional sentinels so argument arity stays stable, mirroring how
            // Handlebars passed unresolved paths through as values whose text form was the path itself.
            return $"$undefined{position}";
        }

        return value.ToObjectValue();
    }

    private sealed class CompiledFluidTemplate(FluidTemplateCompilerEngine owner, IFluidTemplate template) : ITemplate, IRootedTemplate
    {
        public ValueTask<string> RenderAsync(IReadOnlyDictionary<string, object?> context)
            => RenderCoreAsync(rootModel: null, context);

        public ValueTask<string> RenderRootedAsync(object rootModel, IReadOnlyDictionary<string, object?> namedValues)
        {
            ArgumentNullException.ThrowIfNull(rootModel);
            return RenderCoreAsync(rootModel, namedValues);
        }

        private async ValueTask<string> RenderCoreAsync(
            object? rootModel,
            IReadOnlyDictionary<string, object?> context)
        {
            ArgumentNullException.ThrowIfNull(context);

            TemplateContext templateContext = rootModel is null
                ? new(owner._templateOptions)
                : new(rootModel, owner._templateOptions);
            foreach (KeyValuePair<string, object?> entry in context)
            {
                _ = templateContext.SetValue(entry.Key, RenderContextValueAdapter.Create(entry.Value));
            }

            ITemplateTool[] tools = owner._tools;
            foreach (ITemplateTool tool in tools)
            {
                _ = templateContext.SetValue(
                    tool.Name,
                    new FunctionValue((arguments, _) => InvokeToolAsync(tool, arguments)));
            }

            using StringWriter writer = new();
            await template.RenderAsync(writer, NullEncoder.Default, templateContext).ConfigureAwait(false);
            return writer.ToString();
        }
    }

    /// <summary>
    /// Exposes registered partial sources to Liquid include statements as an in-memory file provider.
    /// </summary>
    private sealed class RegisteredPartialFileProvider(Dictionary<string, string> sources) : IFileProvider
    {
        public IDirectoryContents GetDirectoryContents(string subpath) => NotFoundDirectoryContents.Singleton;

        public IFileInfo GetFileInfo(string subpath)
        {
            return sources.TryGetValue(subpath, out string? source)
                ? new PartialFileInfo(subpath, source)
                : new NotFoundFileInfo(subpath);
        }

        public IChangeToken Watch(string filter) => NullChangeToken.Singleton;
    }

    private sealed class PartialFileInfo(string name, string source) : IFileInfo
    {
        // Registered partial sources are immutable per name, so a fixed timestamp keeps Fluid's template cache
        // entries stable across renders.
        private static readonly DateTimeOffset _fixedLastModified = DateTimeOffset.FromUnixTimeSeconds(0);

        public bool Exists => true;

        public bool IsDirectory => false;

        public DateTimeOffset LastModified => _fixedLastModified;

        public long Length => Encoding.UTF8.GetByteCount(source);

        public string? PhysicalPath => null;

        public string Name { get; } = name;

        public Stream CreateReadStream() => new MemoryStream(Encoding.UTF8.GetBytes(source));
    }
}

/// <summary>
/// Adapts caller-supplied render-context values into Fluid values when the template context is constructed.
/// </summary>
internal static class RenderContextValueAdapter
{
    /// <summary>
    /// Adapts one render-context value so nested dictionaries resolve through dot paths and collections iterate.
    /// </summary>
    /// <param name="value">Caller-supplied render-context value.</param>
    /// <returns>The adapted Fluid value.</returns>
    public static FluidValue Create(object? value)
    {
        return value switch
        {
            null => NilValue.Instance,
            string text => StringValue.Create(text),
            bool flag => BooleanValue.Create(flag),
            sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal
                => NumberValue.Create(Convert.ToDecimal(value)),
            IReadOnlyDictionary<string, object?> dictionary => CreateDictionaryValue(dictionary),
            IEnumerable<object?> sequence => CreateArrayValue(sequence),
            _ => new ObjectValue(value),
        };
    }

    private static FluidValue CreateDictionaryValue(IReadOnlyDictionary<string, object?> dictionary)
        => new DictionaryValue(new ReadOnlyDictionaryIndexable(dictionary));

    private static ArrayValue CreateArrayValue(IEnumerable<object?> sequence)
        => new([.. sequence.Select(Create)]);

    private sealed class ReadOnlyDictionaryIndexable(IReadOnlyDictionary<string, object?> dictionary) : IFluidIndexable
    {
        public int Count => dictionary.Count;

        public IEnumerable<string> Keys => dictionary.Keys;

        public bool TryGetValue(string name, out FluidValue value)
        {
            if (dictionary.TryGetValue(name, out object? rawValue))
            {
                value = Create(rawValue);
                return true;
            }

            value = NilValue.Instance;
            return false;
        }
    }
}
