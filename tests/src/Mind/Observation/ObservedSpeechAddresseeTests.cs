using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Mind.Attention;
using AlleyCat.Mind.Observation;
using AlleyCat.Vision;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Mind.Observation;

/// <summary>
/// Unit coverage for the observer-relative speech-addressee policy: state derivation and the per-state
/// importance, fresh-turn, attention, and rendering mapping (AI-001 TR-13..15).
/// </summary>
public sealed class ObservedSpeechAddresseeTests
{
    private const float DefaultObservationImportanceThreshold = 1f;

    /// <summary>The addressee state derives from actor identity and the observer-relative set exactly.</summary>
    [Fact]
    public void GetAddresseeState_MapsEveryStateExactly()
    {
        FakeCharacter owner = new()
        {
            Id = "owner"
        };
        ObservationContext context = new(owner);

        Assert.Equal(SpeechAddresseeState.Self, new ObservedSpeech("char:owner", "Hi").GetAddresseeState(context));
        // Self dominates even when a set is present.
        Assert.Equal(
            SpeechAddresseeState.Self,
            new ObservedSpeech("char:owner", "Hi", ["char:bystander"]).GetAddresseeState(context));
        Assert.Equal(
            SpeechAddresseeState.AddressedToMe,
            new ObservedSpeech("char:speaker", "Hi", ["char:owner"]).GetAddresseeState(context));
        Assert.Equal(
            SpeechAddresseeState.AddressedToMe,
            new ObservedSpeech("char:speaker", "Hi", ["char:bystander", "char:owner"]).GetAddresseeState(context));
        Assert.Equal(
            SpeechAddresseeState.AddressedToAnother,
            new ObservedSpeech("char:speaker", "Hi", ["char:bystander"]).GetAddresseeState(context));
        Assert.Equal(
            SpeechAddresseeState.Undecided,
            new ObservedSpeech("char:speaker", "Hi").GetAddresseeState(context));
        // Unknown speakers are always undecided, even when a set is supplied.
        Assert.Equal(
            SpeechAddresseeState.Undecided,
            new ObservedSpeech(null, "Hi", ["char:owner"]).GetAddresseeState(context));
        Assert.Empty(new ObservedSpeech(null, "Hi", ["char:owner"]).Addressees);
    }

    /// <summary>Importance maps per state with the contractual ordering against Mind's default threshold of 1.</summary>
    [Fact]
    public void CalculateImportance_MapsPerStateAndSatisfiesThresholdOrdering()
    {
        FakeCharacter owner = new()
        {
            Id = "owner"
        };
        ObservationContext context = new(owner);

        float self = new ObservedSpeech("char:owner", "Hi").CalculateImportance(context);
        float toMe = new ObservedSpeech("char:speaker", "Hi", ["char:owner"]).CalculateImportance(context);
        float undecided = new ObservedSpeech("char:speaker", "Hi").CalculateImportance(context);
        float undecidedUnknown = new ObservedSpeech(null, "Hi").CalculateImportance(context);
        float toAnother = new ObservedSpeech("char:speaker", "Hi", ["char:bystander"]).CalculateImportance(context);

        Assert.Equal(0f, self);
        Assert.Equal(2f, toMe);
        Assert.Equal(1f, undecided);
        Assert.Equal(1f, undecidedUnknown);
        Assert.Equal(0.25f, toAnother);
        // To-me is high; undecided is notable and crosses the default threshold; to-another accumulates below it.
        Assert.True(toMe >= DefaultObservationImportanceThreshold);
        Assert.True(undecided >= DefaultObservationImportanceThreshold);
        Assert.True(toAnother < DefaultObservationImportanceThreshold);
    }

    /// <summary>Only addressed-to-me speech requires a fresh turn; every other state never claims the turn.</summary>
    [Fact]
    public void RequiresFreshTurn_IsTrueOnlyForAddressedToMe()
    {
        FakeCharacter owner = new()
        {
            Id = "owner"
        };
        ObservationContext context = new(owner);

        Assert.False(new ObservedSpeech("char:owner", "Hi").RequiresFreshTurn(context));
        Assert.True(new ObservedSpeech("char:speaker", "Hi", ["char:owner"]).RequiresFreshTurn(context));
        Assert.False(new ObservedSpeech("char:speaker", "Hi").RequiresFreshTurn(context));
        Assert.False(new ObservedSpeech(null, "Hi").RequiresFreshTurn(context));
        Assert.False(new ObservedSpeech("char:speaker", "Hi", ["char:bystander"]).RequiresFreshTurn(context));
    }

    /// <summary>Attention effects map per state: raised speaker for to-me, flat speaker for undecided, base speaker plus smaller per-addressee for overheard, none for self and unknown.</summary>
    [Fact]
    public void GetAttentionEffects_MapsPerState()
    {
        FakeCharacter owner = new()
        {
            Id = "owner"
        };
        ObservationContext context = new(owner);

        Assert.Empty(new ObservedSpeech("char:owner", "Hi").GetAttentionEffects(context));
        Assert.Empty(new ObservedSpeech(null, "Hi").GetAttentionEffects(context));

        AttentionEffect undecidedEffect = Assert.Single(
            new ObservedSpeech("char:speaker", "Hi").GetAttentionEffects(context));
        Assert.Equal("char:speaker", undecidedEffect.SubjectFullId);
        Assert.Equal(0.5f, undecidedEffect.Contribution);

        AttentionEffect toMeEffect = Assert.Single(
            new ObservedSpeech("char:speaker", "Hi", ["char:owner"]).GetAttentionEffects(context));
        Assert.Equal("char:speaker", toMeEffect.SubjectFullId);
        Assert.Equal(0.75f, toMeEffect.Contribution);
        Assert.True(toMeEffect.Contribution > 0.5f);

        AttentionEffect[] overheard =
        [
            .. new ObservedSpeech("char:speaker", "Hi", ["char:first", "char:second"]).GetAttentionEffects(context),
        ];
        Assert.Equal(3, overheard.Length);
        Assert.Equal("char:speaker", overheard[0].SubjectFullId);
        Assert.Equal(0.5f, overheard[0].Contribution);
        Assert.Equal("char:first", overheard[1].SubjectFullId);
        Assert.Equal(0.25f, overheard[1].Contribution);
        Assert.Equal("char:second", overheard[2].SubjectFullId);
        Assert.Equal(0.25f, overheard[2].Contribution);
    }

    /// <summary>Rendering names the addressee per state while undecided wording makes no addressee claim.</summary>
    [Fact]
    public void Render_StatesAddresseePerState()
    {
        FakeCharacter owner = new()
        {
            Id = "owner"
        };

        Assert.Equal("I said: Hi", new ObservedSpeech("char:owner", "Hi").Render(owner));
        Assert.Equal(
            "Heard an unknown speaker say: Hi",
            new ObservedSpeech(null, "Hi").Render(owner));
        Assert.Equal(
            "Heard `char:speaker` say: Hi",
            new ObservedSpeech("char:speaker", "Hi").Render(owner));
        Assert.Equal(
            "Heard `char:speaker` say to me: Hi",
            new ObservedSpeech("char:speaker", "Hi", ["char:owner"]).Render(owner));
        Assert.Equal(
            "Heard `char:speaker` say to `char:bystander`: Hi",
            new ObservedSpeech("char:speaker", "Hi", ["char:bystander"]).Render(owner));
        Assert.Equal(
            "Heard `char:speaker` say to `char:first` and `char:second`: Hi",
            new ObservedSpeech("char:speaker", "Hi", ["char:first", "char:second"]).Render(owner));
        Assert.Equal(
            "Heard `char:speaker` say to `char:first`, `char:second` and `char:third`: Hi",
            new ObservedSpeech("char:speaker", "Hi", ["char:first", "char:second", "char:third"]).Render(owner));
    }

    /// <summary>The addressee set is an immutable defensive copy: caller-side mutation never rewrites it.</summary>
    [Fact]
    public void Addressees_AreValidatedCopiedAndImmutable()
    {
        FakeCharacter owner = new()
        {
            Id = "owner"
        };
        string[] source = ["char:first", "char:second"];
        var observation = new ObservedSpeech("char:speaker", "Hi", source);
        source[0] = "char:mutated";

        Assert.Equal(["char:first", "char:second"], observation.Addressees);

        Assert.Empty(new ObservedSpeech("char:speaker", "Hi").Addressees);
        Assert.Empty(new ObservedSpeech("char:speaker", "Hi", null).Addressees);

        _ = Assert.Throws<ArgumentException>(() => new ObservedSpeech("char:speaker", "Hi", ["speaker"]));
        _ = Assert.Throws<ArgumentException>(() => new ObservedSpeech("char:speaker", "Hi", [""]));
        _ = Assert.Throws<ArgumentException>(() => new ObservedSpeech("char:speaker", "Hi", ["char:same", "char:same"]));
        _ = Assert.Throws<ArgumentException>(() => new ObservedSpeech("char:speaker", "Hi", ["char:first", null!]));
    }

    /// <summary>Record with-cloning preserves the validated addressee set.</summary>
    [Fact]
    public void Addressees_SurviveWithCloning()
    {
        var observation = new ObservedSpeech("char:speaker", "Hi", ["char:owner"]);

        ObservedSpeech clone = observation with
        {
            ActorId = "char:other"
        };

        Assert.Equal(["char:owner"], clone.Addressees);
        Assert.True(clone.RequiresFreshTurn(new ObservationContext(new FakeCharacter { Id = "owner" })));
    }

    private sealed class FakeCharacter : ICharacter
    {
        public string Id { get; set; } = string.Empty;

        public IReadOnlyList<IComponent> Components { get; } = [];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public Transform3D GlobalTransform { get; set; } = Transform3D.Identity;
    }
}
