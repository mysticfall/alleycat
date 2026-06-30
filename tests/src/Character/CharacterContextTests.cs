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
    /// Characters with no authored context sources produce an empty context collection.
    /// </summary>
    [Fact]
    public void GetContext_WithNoSources_ReturnsNoContext()
    {
        AlleyCat.Character.Character character = CreateCharacter();
        var scene = new FakeSceneContext([]);

        IReadOnlyCollection<ContextData> context = character.GetContext(scene, observer: null);

        Assert.Empty(context);
    }

    /// <summary>
    /// Characters with one context source return that source's context data.
    /// </summary>
    [Fact]
    public void GetContext_WithOneSource_ReturnsSourceContext()
    {
        AlleyCat.Character.Character character = CreateCharacter(
            FakeContextSource.Create(new ContextData("Title", "Content")));
        var scene = new FakeSceneContext([character]);

        IReadOnlyCollection<ContextData> context = character.GetContext(scene, observer: null);

        ContextData item = Assert.Single(context);
        Assert.Equal(new ContextData("Title", "Content"), item);
    }

    /// <summary>
    /// Characters with multiple context sources aggregate entries in authored source order.
    /// </summary>
    [Fact]
    public void GetContext_WithMultipleSources_AggregatesInAuthoredOrder()
    {
        AlleyCat.Character.Character character = CreateCharacter(
            [
                FakeContextSource.Create(new ContextData("First", "One"), new ContextData("Second", "Two")),
                FakeContextSource.Create(new ContextData("Third", "Three")),
            ]);
        var scene = new FakeSceneContext([character]);

        IReadOnlyCollection<ContextData> context = character.GetContext(scene, observer: null);

        Assert.Equal(
            [
                new ContextData("First", "One"),
                new ContextData("Second", "Two"),
                new ContextData("Third", "Three"),
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

        IReadOnlyCollection<ContextData> context = ((IContextSource)source).GetContext(subject, scene, observer);

        Assert.Equal([new ContextData("Bridge", "Typed")], context);
        Assert.Same(subject, source.Subject);
        Assert.Same(scene, source.Scene);
        Assert.Same(observer, source.Observer);
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
        private ContextData[] _context = [];

        public static FakeContextSource Create(params ContextData[] context)
        {
            var source = (FakeContextSource)RuntimeHelpers.GetUninitializedObject(typeof(FakeContextSource));
            source._context = context;

            return source;
        }

        public override IReadOnlyCollection<ContextData> GetContext(
            IContextual subject,
            ISceneContext scene,
            ICharacter? observer)
            => GetContext(RequireCompatibleSubject<ICharacter>(subject), scene, observer);

        public IReadOnlyCollection<ContextData> GetContext(
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

        public override IReadOnlyCollection<ContextData> GetContext(
            IContextual subject,
            ISceneContext scene,
            ICharacter? observer)
            => GetContext(RequireCompatibleSubject<ICharacter>(subject), scene, observer);

        public IReadOnlyCollection<ContextData> GetContext(
            ICharacter subject,
            ISceneContext scene,
            ICharacter? observer)
        {
            Subject = subject;
            Scene = scene;
            Observer = observer;

            return [];
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

        public IReadOnlyCollection<ContextData> GetContext(
            ICharacter subject,
            ISceneContext scene,
            ICharacter? observer)
        {
            Subject = subject;
            Scene = scene;
            Observer = observer;

            return [new ContextData("Bridge", "Typed")];
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

        public IReadOnlyCollection<ContextData> GetContext(ISceneContext scene, ICharacter? observer)
            => [];
    }

    private sealed class FakeContextual : IContextual
    {
        public IReadOnlyCollection<ContextData> GetContext(ISceneContext scene, ICharacter? observer)
            => [];
    }
}
