using System.Reflection;
using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Templating;
using AlleyCat.Vision;
using Fluid;
using Xunit;

namespace AlleyCat.Tests.Templating;

/// <summary>
/// Unit coverage for the curated Fluid member-access policy: interface composition, sealed surfaces, the
/// single-level rule, the permissive fallback, and deterministic discovery across repeated engine construction.
/// </summary>
public sealed class CuratedTemplateMemberAccessStrategyTests
{
    /// <summary>
    /// The curated annotations in the game assembly cover exactly <see cref="IIdentifiable.FullId" /> and seal
    /// <see cref="ICharacter" /> (AI-003 TR-30 surface, expressed systemically).
    /// </summary>
    [Fact]
    public void CuratedAnnotationsCoverExactlyTheIdentifiableFullIdMember()
    {
        Assert.NotNull(typeof(IIdentifiable).GetProperty(nameof(IIdentifiable.FullId))!
            .GetCustomAttribute<TemplateExposedAttribute>());
        Assert.Null(typeof(IIdentifiable).GetProperty(nameof(IIdentifiable.Id))!
            .GetCustomAttribute<TemplateExposedAttribute>());
        Assert.Null(typeof(IIdentifiable).GetProperty(nameof(IIdentifiable.Type))!
            .GetCustomAttribute<TemplateExposedAttribute>());
        Assert.NotNull(typeof(ICharacter).GetCustomAttribute<TemplateSealedAttribute>());
    }

    /// <summary>
    /// A member curated on an interface resolves through a compiled template rendering a concrete implementer,
    /// without any per-type registration (pinned Fluid interface walk).
    /// </summary>
    [Fact]
    public async Task CuratedInterfaceMemberResolvesThroughConcreteImplementer()
    {
        FluidTemplateCompilerEngine compiler = new();

        ITemplate template = compiler.Compile("{{ subject.FullId }}");

        string result = await template.RenderAsync(new Dictionary<string, object?>
        {
            ["subject"] = new FakeIdentifiable { Id = "widget", Type = "item" },
        });

        Assert.Equal("item:widget", result);
    }

    /// <summary>
    /// A sealed interface renders exactly its curated members: <c>FullId</c> (curated on the base
    /// <see cref="IIdentifiable" /> interface) resolves while <c>Id</c>, <c>Type</c>, and public runtime
    /// properties such as <c>Components</c> render nil for an <see cref="ICharacter" /> implementer.
    /// </summary>
    [Fact]
    public async Task SealedCharacterSurfaceRendersExactlyItsCuratedMembers()
    {
        FluidTemplateCompilerEngine compiler = new();

        ITemplate template = compiler.Compile(
            "[{{ character.FullId }}]|[{{ character.Id }}]|[{{ character.Type }}]|" +
            "[{{ character.Components }}]|[{{ character.VisualCues }}]");

        string result = await template.RenderAsync(new Dictionary<string, object?>
        {
            ["character"] = new FakeCharacter { Id = "fake_character" },
        });

        Assert.Equal("[char:fake_character]|[]|[]|[]|[]", result);
    }

    /// <summary>
    /// Objects without curated or sealed registrations keep permissive reflective access to their public
    /// properties through a compiled template (TMPL-001 TR-11 behaviour preserved).
    /// </summary>
    [Fact]
    public async Task UnregisteredObjectsKeepPermissivePublicMemberAccess()
    {
        FluidTemplateCompilerEngine compiler = new();

        ITemplate template = compiler.Compile("{{ record.Content }}");

        string result = await template.RenderAsync(new Dictionary<string, object?>
        {
            ["record"] = new PlainRecord { Content = "knock" },
        });

        Assert.Equal("knock", result);
    }

    /// <summary>
    /// Constructing the engine repeatedly resolves one shared discovered strategy instance and renders
    /// deterministically: discovery never duplicate-registers or throws on repeat instantiation.
    /// </summary>
    [Fact]
    public async Task RepeatedEngineConstructionRendersDeterministically()
    {
        FluidTemplateCompilerEngine first = new();
        FluidTemplateCompilerEngine second = new();

        Assert.Same(CuratedTemplateMemberAccessStrategy.Shared, CuratedTemplateMemberAccessStrategy.Shared);

        Dictionary<string, object?> context = new()
        {
            ["character"] = new FakeCharacter { Id = "fake_character" }
        };
        ITemplate firstTemplate = first.Compile("{{ character.FullId }}|{{ character.Id }}");
        ITemplate secondTemplate = second.Compile("{{ character.FullId }}|{{ character.Id }}");

        Assert.Equal("char:fake_character|", await firstTemplate.RenderAsync(context));
        Assert.Equal(await firstTemplate.RenderAsync(context), await secondTemplate.RenderAsync(context));
    }

    /// <summary>
    /// Curating the same member name on two interfaces fails loudly (the single-level rule) because Fluid's
    /// interface enumeration order is unspecified and duplicates would resolve ambiguously.
    /// </summary>
    [Fact]
    public void RegisteringTheSameCuratedNameOnTwoInterfacesThrows()
    {
        CuratedTemplateMemberAccessStrategy strategy = new();
        strategy.RegisterCuratedMember(typeof(IFirstSynthetic).GetProperty(nameof(IFirstSynthetic.Label))!);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            strategy.RegisterCuratedMember(typeof(ISecondSynthetic).GetProperty(nameof(ISecondSynthetic.Label))!));

        Assert.Contains(nameof(IFirstSynthetic), exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ISecondSynthetic), exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Registering the same curated member on the same interface twice is idempotent rather than a failure.
    /// </summary>
    [Fact]
    public void RegisteringTheSameCuratedMemberTwiceIsIdempotent()
    {
        CuratedTemplateMemberAccessStrategy strategy = new();
        PropertyInfo label = typeof(IFirstSynthetic).GetProperty(nameof(IFirstSynthetic.Label))!;
        strategy.RegisterCuratedMember(label);

        Exception? exception = Record.Exception(() => strategy.RegisterCuratedMember(label));

        Assert.Null(exception);
    }

    /// <summary>
    /// A directly sealed synthetic interface hides its unlisted members (including value-type properties) while
    /// its curated member still renders. This exercises the registration surface independently of game-assembly
    /// discovery, which only scans the game assembly.
    /// </summary>
    [Fact]
    public async Task DirectlySealedSyntheticInterfaceHidesUnlistedMembers()
    {
        CuratedTemplateMemberAccessStrategy strategy = new();
        strategy.RegisterCuratedMember(typeof(IShapeSynthetic).GetProperty(nameof(IShapeSynthetic.Name))!);
        strategy.SealInterface(typeof(IShapeSynthetic));

        TemplateOptions options = new()
        {
            MemberAccessStrategy = strategy,
            ModelNamesComparer = StringComparer.Ordinal,
            StrictVariables = false,
        };
        FluidParser parser = new();
        IFluidTemplate template = parser.Parse(
            "[{{ shape.Name }}]|[{{ shape.Other }}]|[{{ shape.Radius }}]");

        TemplateContext context = new(options);
        _ = context.SetValue("shape", new SyntheticShape());

        Assert.Equal("[circle]|[]|[]", await template.RenderAsync(context));
    }

    private sealed class FakeIdentifiable : IIdentifiable
    {
        public string Id
        {
            get; set;
        } = "sample";

        public string Type
        {
            get; set;
        } = "kind";
    }

    private sealed class FakeCharacter : ICharacter
    {
        public string Id
        {
            get; set;
        } = "fake_character";

        public IReadOnlyList<IComponent> Components { get; } = [];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];
    }

    private sealed class PlainRecord
    {
        public string Content
        {
            get; set;
        } = "knock";
    }

    private interface IFirstSynthetic
    {
        string Label
        {
            get;
        }
    }

    private interface ISecondSynthetic
    {
        string Label
        {
            get;
        }
    }

    private interface IShapeSynthetic
    {
        string Name
        {
            get;
        }
    }

    private sealed class SyntheticShape : IShapeSynthetic
    {
        public string Name => "circle";

        public static string Other => "hidden";

        public static double Radius => 1.5;
    }
}
