using System.Runtime.CompilerServices;
using AlleyCat.Character;
using AlleyCat.Context;
using AlleyCat.Scene;
using Xunit;

namespace AlleyCat.Tests.Character;

/// <summary>
/// Unit coverage for character contextual-information aggregation.
/// </summary>
public sealed class CharacterContextTests
{
    /// <summary>
    /// Characters with no authored context sources produce an empty context dictionary.
    /// </summary>
    [Fact]
    public void GetContext_WithNoSources_ReturnsNoContext()
    {
        AlleyCat.Character.Character character = CreateCharacter();
        var scene = new FakeSceneContext([]);

        IReadOnlyDictionary<string, object?> context = character.GetContext(scene, observer: null);

        Assert.Empty(context);
    }

    /// <summary>
    /// Characters with one context source return that source's context dictionary.
    /// </summary>
    [Fact]
    public void GetContext_WithOneSource_ReturnsSourceContext()
    {
        AlleyCat.Character.Character character = CreateCharacter(FakeContextSource.Create(
            new Dictionary<string, object?>
            {
                ["title"] = "Title",
                ["count"] = 3,
            }));
        var scene = new FakeSceneContext([character]);

        IReadOnlyDictionary<string, object?> context = character.GetContext(scene, observer: null);

        Assert.Equal(2, context.Count);
        Assert.Equal("Title", context["title"]);
        Assert.Equal(3, context["count"]);
    }

    /// <summary>
    /// Characters with multiple context sources aggregate entries in authored source order.
    /// </summary>
    [Fact]
    public void GetContext_WithMultipleSources_AggregatesInAuthoredOrder()
    {
        AlleyCat.Character.Character character = CreateCharacter(
            [
                FakeContextSource.Create(new Dictionary<string, object?>
                {
                    ["first"] = "One",
                    ["second"] = "Two",
                }),
                FakeContextSource.Create(new Dictionary<string, object?>
                {
                    ["third"] = "Three",
                }),
            ]);
        var scene = new FakeSceneContext([character]);

        IReadOnlyDictionary<string, object?> context = character.GetContext(scene, observer: null);

        Assert.Equal(
            [
                new KeyValuePair<string, object?>("first", "One"),
                new KeyValuePair<string, object?>("second", "Two"),
                new KeyValuePair<string, object?>("third", "Three"),
            ],
            context);
    }

    /// <summary>
    /// Character context aggregation passes the subject, scene, and observer to each source.
    /// </summary>
    [Fact]
    public void GetContext_PassesSubjectSceneAndObserverToSource()
    {
        var source = CapturingContextSource.Create();
        AlleyCat.Character.Character character = CreateCharacter(source);
        var observer = new FakeCharacter();
        var scene = new FakeSceneContext([character, observer]);

        _ = character.GetContext(scene, observer);

        Assert.Same(character, source.Subject);
        Assert.Same(scene, source.Scene);
        Assert.Same(observer, source.Observer);
    }

    /// <summary>
    /// Typed context source bridge delegates compatible non-generic calls to the typed implementation.
    /// </summary>
    [Fact]
    public void TypedContextSourceBridge_WithCompatibleSubject_DelegatesToTypedImplementation()
    {
        var source = new TypedBridgeContextSource();
        var subject = new FakeCharacter();
        var scene = new FakeSceneContext([subject]);
        var observer = new FakeCharacter();

        IReadOnlyDictionary<string, object?> context = ((IContextSource)source).GetContext(subject, scene, observer);

        KeyValuePair<string, object?> item = Assert.Single(context);
        Assert.Equal(new KeyValuePair<string, object?>("bridge", "Typed"), item);
        Assert.Same(subject, source.Subject);
        Assert.Same(scene, source.Scene);
        Assert.Same(observer, source.Observer);
    }

    /// <summary>
    /// Duplicate keys from multiple authored sources fail fast instead of overwriting earlier entries.
    /// </summary>
    [Fact]
    public void GetContext_WithDuplicateSourceKeys_ThrowsClearException()
    {
        AlleyCat.Character.Character character = CreateCharacter(
            [
                FakeContextSource.Create(new Dictionary<string, object?> { ["name"] = "First" }),
                FakeContextSource.Create(new Dictionary<string, object?> { ["name"] = "Second" }),
            ]);
        var scene = new FakeSceneContext([character]);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => character.GetContext(scene, observer: null));

        Assert.Contains("duplicate context key 'name'", exception.Message);
    }

    /// <summary>
    /// Typed context source bridge rejects incompatible non-generic subject calls with clear type details.
    /// </summary>
    [Fact]
    public void TypedContextSourceBridge_WithIncompatibleSubject_ThrowsClearException()
    {
        var source = new TypedBridgeContextSource();
        var subject = new FakeContextual();
        var scene = new FakeSceneContext([]);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ((IContextSource)source).GetContext(subject, scene, observer: null));

        Assert.Contains(typeof(ICharacter).FullName!, exception.Message);
        Assert.Contains(typeof(FakeContextual).FullName!, exception.Message);
    }

    private sealed class FakeContextSource : ContextSource, IContextSource<ICharacter>
    {
        private IReadOnlyDictionary<string, object?> _context = new Dictionary<string, object?>();

        public static FakeContextSource Create(IReadOnlyDictionary<string, object?> context)
        {
            var source = (FakeContextSource)RuntimeHelpers.GetUninitializedObject(typeof(FakeContextSource));
            source._context = context;

            return source;
        }

        public override IReadOnlyDictionary<string, object?> GetContext(
            IContextual subject,
            ISceneContext scene,
            ICharacter? observer)
            => GetContext(RequireCompatibleSubject<ICharacter>(subject), scene, observer);

        public IReadOnlyDictionary<string, object?> GetContext(
            ICharacter subject,
            ISceneContext scene,
            ICharacter? observer)
            => _context;
    }

    private static AlleyCat.Character.Character CreateCharacter(params ContextSource[] sources)
    {
        var character = (AlleyCat.Character.Character)RuntimeHelpers.GetUninitializedObject(
            typeof(AlleyCat.Character.Character));
        character.ContextSources = sources;

        return character;
    }

    private sealed class CapturingContextSource : ContextSource, IContextSource<ICharacter>
    {
        public static CapturingContextSource Create()
            => (CapturingContextSource)RuntimeHelpers.GetUninitializedObject(typeof(CapturingContextSource));

        public ICharacter? Subject
        {
            get; private set;
        }

        public ISceneContext? Scene
        {
            get; private set;
        }

        public ICharacter? Observer
        {
            get; private set;
        }

        public override IReadOnlyDictionary<string, object?> GetContext(
            IContextual subject,
            ISceneContext scene,
            ICharacter? observer)
            => GetContext(RequireCompatibleSubject<ICharacter>(subject), scene, observer);

        public IReadOnlyDictionary<string, object?> GetContext(
            ICharacter subject,
            ISceneContext scene,
            ICharacter? observer)
        {
            Subject = subject;
            Scene = scene;
            Observer = observer;

            return new Dictionary<string, object?>();
        }
    }

    private sealed class TypedBridgeContextSource : IContextSource<ICharacter>
    {
        public ICharacter? Subject
        {
            get; private set;
        }

        public ISceneContext? Scene
        {
            get; private set;
        }

        public ICharacter? Observer
        {
            get; private set;
        }

        public IReadOnlyDictionary<string, object?> GetContext(
            ICharacter subject,
            ISceneContext scene,
            ICharacter? observer)
        {
            Subject = subject;
            Scene = scene;
            Observer = observer;

            return new Dictionary<string, object?>
            {
                ["bridge"] = "Typed",
            };
        }
    }

    private sealed record FakeSceneContext(IReadOnlyCollection<ICharacter> Characters) : ISceneContext;

    private sealed class FakeCharacter : ICharacter
    {
        public string Id
        {
            get; set;
        } = "FakeCharacter";

        public IReadOnlyList<AlleyCat.Core.IComponent> Components { get; } = [];

        public IReadOnlyDictionary<string, object?> GetContext(ISceneContext scene, ICharacter? observer)
            => new Dictionary<string, object?>();
    }

    private sealed class FakeContextual : IContextual
    {
        public IReadOnlyDictionary<string, object?> GetContext(ISceneContext scene, ICharacter? observer)
            => new Dictionary<string, object?>();
    }
}
