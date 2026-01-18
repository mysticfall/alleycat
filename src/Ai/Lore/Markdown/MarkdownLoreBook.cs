using AlleyCat.Common;
using AlleyCat.Env;
using AlleyCat.Io;
using LanguageExt;
using LanguageExt.Common;
using Markdig;
using Markdig.Extensions.Yaml;
using Markdig.Renderers.Roundtrip;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using static LanguageExt.Prelude;

namespace AlleyCat.Ai.Lore.Markdown;

public class MarkdownLoreBook : ILoreBook
{
    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .UseYamlFrontMatter()
        .EnableTrackTrivia()
        .Build();

    private static readonly IDeserializer YamlParser = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    private readonly record struct Metadata(
        string Id,
        int? Order,
        bool Essential
    );

    private class LoreEntryRenderer : RoundtripRenderer
    {
        public int Depth { get; set; } = 1;
        public int BaseLevel { get; set; } = 1;

        public LoreEntryRenderer(TextWriter writer) : base(writer)
        {
            ObjectRenderers.Insert(0, new OffsetHeadingRenderer(this));
        }

        private class OffsetHeadingRenderer(
            LoreEntryRenderer parent
        ) : RoundtripObjectRenderer<HeadingBlock>
        {
            protected override void Write(RoundtripRenderer renderer, HeadingBlock heading)
            {
                var depth = parent.Depth;
                var baseLevel = parent.BaseLevel;

                var newLevel = Math.Max(1, heading.Level + depth - baseLevel);

                renderer.Write(new string(heading.HeaderChar, newLevel));
                renderer.Write(' ');

                if (heading.Inline != null)
                {
                    renderer.WriteChildren(heading.Inline);
                }

                renderer.WriteLine();
            }
        }
    }

    private readonly Map<LoreId, LoreEntry> _entries;

    private readonly Map<LoreId, MarkdownDocument> _sources;

    private readonly ILogger _logger;

    private Seq<LoreId> GetAncestors(LoreId id) =>
        _entries.Find(id).Bind(x => x.Parent).Match(
            p => GetAncestors(p) + Seq(id),
            () => Seq(id)
        );

    private Eff<LorePath> GetPath(LoreId id) => _entries
        .Find(id)
        .Map(x => new LorePath(GetAncestors(x.Id)))
        .ToEff(Error.New($"No matching source found for id: {id}"));

    public Seq<LoreEntry> TableOfContents { get; }

    public MarkdownLoreBook(
        Seq<MarkdownLoreSource> content,
        ILoggerFactory? loggerFactory = null
    )
    {
        TableOfContents = content.ToTableOfContents();

        _entries = TableOfContents
            .Bind(x => x)
            .AsIterable()
            .Map(x => (x.Id, x))
            .ToMap();
        _sources = content
            .Map(x => (x.Id, x.Document))
            .AsIterable()
            .ToMap();

        _logger = (loggerFactory ?? NullLoggerFactory.Instance)
            .CreateLogger<MarkdownLoreBook>();
    }

    public Eff<IEnv, LoreText> GetContents(params Seq<LoreId> ids) =>
        from paths in (ids.IsEmpty ? _sources.Keys : ids.AsIterable())
            .ToSeq()
            .Traverse(GetPath)
            .As()
        let selected = TableOfContents.Bind(x => x.Filter(paths).ToSeq())
        let writer = new StringWriter()
        let renderer = new LoreEntryRenderer(writer)
        from _ in liftEff(() => selected.Iter(x => Render(x, renderer)))
        from text in LoreText.Create(writer.ToString()).ToEff(identity)
        select text;

    private void Render(LoreEntry entry, LoreEntryRenderer renderer, int depth = 1)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Rendering entry: [{id}] {title}", entry.Id, entry.Title);
        }

        var source = _sources[entry.Id];
        var document = source.AsIterable();

        var baseLevel = document.Fold(int.MaxValue, (minLevel, b) =>
        {
            if (b is HeadingBlock heading)
            {
                return Math.Min(heading.Level, minLevel);
            }

            return minLevel;
        });

        document.Iter(block =>
        {
            switch (block)
            {
                case HeadingBlock:
                {
                    renderer.Depth = depth;
                    renderer.BaseLevel = baseLevel;

                    renderer.Render(block);

                    break;
                }
                case YamlFrontMatterBlock:
                    break;
                default:
                    renderer.Render(block);
                    break;
            }
        });

        renderer.WriteLine();

        entry.Children.Iter(c => Render(c, renderer, depth + 1));
    }

    private static Eff<MarkdownLoreSource> ParseEntry(
        ResourcePath path,
        string source
    ) =>
        from document in liftEff(() => Markdig.Markdown.Parse(source, MarkdownPipeline))
        from metadata in document
            .Descendants<YamlFrontMatterBlock>()
            .AsIterable()
            .Head
            .Map(x => source.Substring(x.Span.Start, x.Span.Length).Replace("---", ""))
            .Traverse(x => liftEff(() => YamlParser.Deserialize<Metadata>(x)))
        let firstHeading = document
            .Descendants<HeadingBlock>()
            .Bind(x => Optional(x.Inline).ToSeq())
            .Bind(x => x.Descendants<LiteralInline>())
            .AsIterable()
            .Head
            .Map(x => x.Content.ToString().Trim())
            .Filter(x => !string.IsNullOrWhiteSpace(x))
        let fileName = Path.GetFileNameWithoutExtension(path)
        from id in LoreId
            .Create(metadata.Map(x => x.Id).IfNone(fileName))
            .ToEff(identity)
        from title in LoreTitle
            .Create(firstHeading.IfNone(fileName.ToTitleCase))
            .ToEff(identity)
        let order = metadata.Map(x => x.Order).IfNone(int.MaxValue)
        let essential = metadata.Map(x => x.Essential).IfNone(false)
        from result in liftEff(() =>
            new MarkdownLoreSource(path, id, title, order ?? int.MaxValue, essential, document)
        )
        select result;

    private static Eff<IEnv, MarkdownLoreSource> CreateEntry(
        ResourcePath path,
        ILogger logger
    ) =>
        from _ in liftEff(() =>
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("Reading lore content from {path}", path);
            }
        })
        from env in runtime<IEnv>()
        let io = env.FileProvider
        from source in io.ReadAllText(path)
        from entry in ParseEntry(path, source)
        select entry;

    private static Eff<IEnv, Seq<MarkdownLoreSource>> CreateEntries(
        ResourcePath path,
        ILogger logger
    ) =>
        from _ in liftEff(() =>
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("Searching for sources in {path}", path);
            }
        })
        from env in runtime<IEnv>()
        let io = env.FileProvider
        from children in liftEff(() =>
            io.GetDirectoryContents(path)
                .AsIterable()
                .ToSeq()
                .Partition(x => x.IsDirectory)
        )
        let dirs = children.First.Map(x => x.Name)
        let files = children.Second.Map(x => x.Name)
        let sources = files
            .Filter(x => x.EndsWith(".md"))
            .Map(x => path + x)
        from fromDirs in dirs
            .Traverse(x => CreateEntries(path + x, logger))
            .Map(x => x.Flatten())
        from fromFiles in sources
            .Traverse(x => CreateEntry(x, logger))
        select fromDirs + fromFiles;

    public static Eff<IEnv, ILoreBook> Create(
        ResourcePath path,
        ILoggerFactory? loggerFactory = null
    ) => (
        from logger in liftEff(() =>
            (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<MarkdownLoreBook>()
        )
        from _ in liftEff(() =>
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Creating a lorebook from: {path}", path);
            }
        })
        from content in CreateEntries(path, logger)
        select (ILoreBook)new MarkdownLoreBook(content, loggerFactory)
    ).As();
}