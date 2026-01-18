using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AlleyCat.Actor.Action;
using AlleyCat.Ai.Lore;
using AlleyCat.Env;
using AlleyCat.Logging;
using LanguageExt;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Ai.Agent.Tool;

public class LoreTool(
    ILoreBook loreBook,
    ILoggerFactory? loggerFactory = null
) : IAgentTool
{
    public AiFunctionName? Name => null;

    public Delegate Delegate => ReadLore;

    public Option<JsonSerializerOptions> SerialiserOptions { get; } = Some(new JsonSerializerOptions
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
            .WithAddedModifier(LoreToolResponse.PolymorphicModifier)
    });

    private readonly ILogger _logger = loggerFactory.GetLogger<LoreTool>();

    [DisplayName("read_lore")]
    [Description("Read lore entries.")]
    private async Task<LoreToolResponse> ReadLore(
        [Description("A list of the entries to read (e.g. ['world', 'race'])")]
        string[] ids,
        IServiceProvider services
    )
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Reading lore entries: {entries}", string.Join(", ", ids));
        }

        var env = services.GetRequiredService<IEnv>();

        var request =
            from selection in ids
                .AsIterable()
                .ToSeq()
                .Traverse(LoreId.Create)
                .As()
                .ToEff(identity)
            from text in loreBook.GetContents(selection)
            select new LoreToolResponse(text);

        var response = await request.As().RunUnsafeAsync(env);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Responding with content:\n{content}", response.Content);
        }

        return response;
    }

    private readonly record struct LoreToolResponse(string Content) : IToolResponse
    {
        public static Action<JsonTypeInfo> PolymorphicModifier { get; } = info =>
        {
            if (info.Type != typeof(IToolResponse)) return;

            var options = info.PolymorphismOptions ??= new JsonPolymorphismOptions();
            var derivedType = new JsonDerivedType(typeof(LoreToolResponse), "lore_entry");

            options.DerivedTypes.Add(derivedType);
        };
    }
}