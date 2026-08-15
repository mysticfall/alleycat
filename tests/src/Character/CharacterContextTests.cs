using System.Runtime.CompilerServices;
using AlleyCat.Character;
using AlleyCat.Context;
using AlleyCat.Core;
using AlleyCat.Scene;
using AlleyCat.Vision;
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
    /// Character cards expose only the contextual character's canonical typed identity.
    /// </summary>
    [Fact]
    public void CharacterCardContextSource_ReturnsOnlySubjectFullId()
    {
        var source = (CharacterCardContextSource)RuntimeHelpers.GetUninitializedObject(
            typeof(CharacterCardContextSource));
        var subject = new FakeCharacter { Id = "case_sensitive_identity" };
        var observer = new FakeCharacter { Id = "observer" };
        var scene = new FakeSceneContext([subject, observer]);

        IReadOnlyDictionary<string, object?> context = source.GetContext(subject, scene, observer);

        KeyValuePair<string, object?> entry = Assert.Single(context);
        Assert.Equal(nameof(IIdentifiable.FullId), entry.Key);
        Assert.Equal("char:case_sensitive_identity", entry.Value);
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
    /// Character context aggregation passes the subject, scene, and identifiable observer to each source.
    /// </summary>
    [Fact]
    public void GetContext_PassesSubjectSceneAndObserverToSource()
    {
        var source = CapturingContextSource.Create();
        AlleyCat.Character.Character character = CreateCharacter(source);
        var observer = new FakeCharacter { Id = "observer" };
        var scene = new FakeSceneContext([character]);

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

    /// <summary>Context sources accept identity inputs without requiring completed-context composition.</summary>
    [Fact]
    public void ContextSourceContract_AcceptsIdentifiableInputsRatherThanContextualInputs()
    {
        Type[] parameterTypes = [.. typeof(IContextSource).GetMethod(nameof(IContextSource.GetContext))!
            .GetParameters()
            .Select(parameter => parameter.ParameterType)];

        Assert.Equal(typeof(IIdentifiable), parameterTypes[0]);
        Assert.Equal(typeof(IIdentifiable), parameterTypes[2]);
        Assert.False(typeof(IContextual).IsAssignableFrom(typeof(IIdentifiable)));
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
        var subject = new FakeIdentifiable();
        var scene = new FakeSceneContext([]);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ((IContextSource)source).GetContext(subject, scene, observer: null));

        Assert.Contains(typeof(ICharacter).FullName!, exception.Message);
        Assert.Contains(typeof(FakeIdentifiable).FullName!, exception.Message);
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
            IIdentifiable subject,
            ISceneContext scene,
            IIdentifiable? observer)
            => GetContext(RequireCompatibleSubject<ICharacter>(subject), scene, observer);

        public IReadOnlyDictionary<string, object?> GetContext(
            ICharacter subject,
            ISceneContext scene,
            IIdentifiable? observer)
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

        public IIdentifiable? Observer
        {
            get; private set;
        }

        public override IReadOnlyDictionary<string, object?> GetContext(
            IIdentifiable subject,
            ISceneContext scene,
            IIdentifiable? observer)
            => GetContext(RequireCompatibleSubject<ICharacter>(subject), scene, observer);

        public IReadOnlyDictionary<string, object?> GetContext(
            ICharacter subject,
            ISceneContext scene,
            IIdentifiable? observer)
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

        public IIdentifiable? Observer
        {
            get; private set;
        }

        public IReadOnlyDictionary<string, object?> GetContext(
            ICharacter subject,
            ISceneContext scene,
            IIdentifiable? observer)
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

    private sealed record FakeSceneContext(IReadOnlyCollection<ICharacter> Characters) : ISceneContext
    {
        public AlleyCat.Core.Content.ContentContext Content => AlleyCat.Core.Content.ContentContext.Default;

        public IIdentifiable? Find(string fullId)
        {
            IdentityValidator.ValidateFullId(fullId, nameof(fullId));
            return Characters.FirstOrDefault(character => string.Equals(character.FullId, fullId, StringComparison.Ordinal));
        }

        public IIdentifiable Resolve(string fullId)
            => Find(fullId) ?? throw new InvalidOperationException($"Current scene does not contain identifiable object '{fullId}'.");
    }

    private sealed class FakeCharacter : ICharacter
    {
        public string Id
        {
            get; set;
        } = "fake_character";

        public IReadOnlyList<IComponent> Components { get; } = [];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public IReadOnlyDictionary<string, object?> GetContext(ISceneContext scene, IContextual? observer)
            => new Dictionary<string, object?>();
    }

    private sealed class FakeIdentifiable : IIdentifiable
    {
        public string Id { get; set; } = "fake_identifiable";

        public string Type => "fake";

    }
}
