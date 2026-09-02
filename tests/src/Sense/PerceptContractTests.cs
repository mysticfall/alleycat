using System.Collections;
using System.Reflection;
using AlleyCat.Sense;
using AlleyCat.Speech;
using AlleyCat.Speech.Voice;
using AlleyCat.Vision;
using Xunit;

namespace AlleyCat.Tests.Sense;

/// <summary>Contract coverage for body-free percept transport and exact perception dispatch.</summary>
public sealed class PerceptContractTests
{
    /// <summary>Speech transport snapshots trimmed text and the raw source ID.</summary>
    [Fact]
    public void SpeechPercept_TrimsContentAndSnapshotsRawVoiceID()
    {
        var percept = new SpeechPercept("  hello  ", "External.Device");

        Assert.Equal("hello", percept.Content);
        Assert.Equal("External.Device", percept.SourceVoiceID);
        Assert.All(typeof(SpeechPercept).GetProperties(), property => Assert.False(property.CanWrite));
        Assert.DoesNotContain(typeof(SpeechPercept).GetProperties(), property =>
            typeof(IVoice).IsAssignableFrom(property.PropertyType)
            || (property.PropertyType.IsClass && property.PropertyType.Namespace?.StartsWith("AlleyCat", StringComparison.Ordinal) == true));
    }

    /// <summary>Automatic segment metadata enforces identity invariants and remains immutable.</summary>
    [Fact]
    public void SpeechSegmentMetadata_EnforcesInvariantIdentityAndDerivedContinuation()
    {
        _ = Assert.Throws<ArgumentNullException>(() => new SpeechSegmentMetadata(null!, 0));
        _ = Assert.Throws<ArgumentException>(() => new SpeechSegmentMetadata(string.Empty, 0));
        _ = Assert.Throws<ArgumentException>(() => new SpeechSegmentMetadata(" ", 0));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new SpeechSegmentMetadata("group", -1));

        var first = new SpeechSegmentMetadata("group", 0);
        var continued = new SpeechSegmentMetadata("group", 1);

        Assert.Equal("group", first.SpeechGroupID);
        Assert.Equal(0, first.SegmentIndex);
        Assert.False(first.Continued);
        Assert.Equal("group", continued.SpeechGroupID);
        Assert.Equal(1, continued.SegmentIndex);
        Assert.True(continued.Continued);
        Assert.All(typeof(SpeechSegmentMetadata).GetProperties(), property => Assert.False(property.CanWrite));
        Assert.DoesNotContain(
            typeof(SpeechSegmentMetadata).GetConstructors(),
            constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType)
                .SequenceEqual([typeof(string), typeof(int), typeof(bool)]));
    }

    /// <summary>Terminal segment settlements carry only immutable identity metadata and a generic kind.</summary>
    [Fact]
    public void SpeechSegmentSettlement_IsTextlessAndImmutable()
    {
        var settlement = new SpeechSegmentSettlement(
            new SpeechSegmentMetadata("group", 1),
            SpeechSegmentSettlementKind.Failed);

        Assert.Equal("group", settlement.Metadata.SpeechGroupID);
        Assert.Equal(1, settlement.Metadata.SegmentIndex);
        Assert.Equal(SpeechSegmentSettlementKind.Failed, settlement.Kind);
        Assert.All(typeof(SpeechSegmentSettlement).GetProperties(), property => Assert.False(property.CanWrite));
        Assert.DoesNotContain(typeof(SpeechSegmentSettlement).GetProperties(), property => property.PropertyType == typeof(string));
    }

    /// <summary>Speech without automatic metadata retains the ungrouped percept defaults.</summary>
    [Fact]
    public void SpeechPercept_WithoutMetadata_RemainsUngrouped()
    {
        var percept = new SpeechPercept("manual or AI speech", "voice:source");

        Assert.Null(percept.SpeechGroupID);
        Assert.Equal(0, percept.SegmentIndex);
        Assert.False(percept.Continued);
    }

    /// <summary>Visual transport owns an ordered read-only identity copy.</summary>
    [Fact]
    public void VisualSurveyPercept_OwnsImmutableOrderedIdentitySnapshot()
    {
        string[] source = ["char:second", "char:first", "char:second"];
        var percept = new VisualSurveyPercept(source);
        source[0] = "char:changed";

        Assert.Equal(["char:second", "char:first", "char:second"], percept.SubjectFullIDs);
        IList mutable = Assert.IsAssignableFrom<IList>(percept.SubjectFullIDs);
        _ = Assert.Throws<NotSupportedException>(() => mutable[0] = "char:changed");
        Assert.All(typeof(VisualSurveyPercept).GetProperties(), property => Assert.False(property.CanWrite));
    }

    /// <summary>Senses expose exact declared types and eyes retains no public scan API.</summary>
    [Fact]
    public void SenseAndEyesContracts_DeclareOnlyExactPerceptsAndNoPublicScan()
    {
        EventInfo perceived = Assert.Single(typeof(ISense).GetEvents());
        Assert.Equal(typeof(Action<IPercept>), perceived.EventHandlerType);
        Assert.Equal(typeof(IReadOnlyList<Type>), typeof(ISense).GetProperty(nameof(ISense.PerceptTypes))!.PropertyType);
        Assert.DoesNotContain(typeof(IVision).GetMethods(), method => method.Name == "Scan");
        Assert.True(typeof(ISense).IsAssignableFrom(typeof(EyesBehaviour)));
        Assert.True(typeof(ISense).IsAssignableFrom(typeof(Hearing)));
        Assert.True(typeof(ISense<IVisualPercept>).IsAssignableFrom(typeof(IVision)));
        Assert.True(typeof(ISense).IsAssignableFrom(typeof(ISense<IVisualPercept>)));
        Assert.True(typeof(ISense<IPercept>).IsAssignableFrom(typeof(ISense<IVisualPercept>)));
        _ = Assert.Single(typeof(ISense).GetEvents());
    }

    /// <summary>Visual percepts expose one family while target transitions retain exact cue identity.</summary>
    [Fact]
    public void VisualPerceptFamily_IncludesSurveyAndImmutableLookTargetTransition()
    {
        var percept = new LookTargetChangedPercept(null, null);

        _ = Assert.IsAssignableFrom<IVisualPercept>(new VisualSurveyPercept([]));
        _ = Assert.IsAssignableFrom<IVisualPercept>(percept);
        Assert.Equal(
            [typeof(VisualCue), typeof(VisualCue)],
            typeof(LookTargetChangedPercept).GetConstructors().Single().GetParameters().Select(parameter => parameter.ParameterType));
        Assert.All(typeof(LookTargetChangedPercept).GetProperties(), property => Assert.False(property.CanWrite));
    }

    /// <summary>Speech ownership remains top-level while voice implementations remain isolated below Voice.</summary>
    [Fact]
    public void HearingContracts_AreTopLevelSpeechCapabilities()
    {
        Assert.Equal("AlleyCat.Speech", typeof(SpeechPercept).Namespace);
        Assert.Equal("AlleyCat.Speech", typeof(IHearing).Namespace);
        Assert.Equal("AlleyCat.Speech", typeof(IHasHearing).Namespace);
        Assert.Equal("AlleyCat.Speech", typeof(Hearing).Namespace);
        Assert.Equal("AlleyCat.Speech.Voice", typeof(IVoice).Namespace);
        Assert.True(typeof(ISense).IsAssignableFrom(typeof(IHearing)));
        Assert.Equal(typeof(IReadOnlyList<Type>), typeof(ISense).GetProperty(nameof(ISense.PerceptTypes))!.PropertyType);
        Assert.Equal(
            [typeof(string), typeof(IVoice), typeof(SpeechSegmentMetadata)],
            typeof(IHearing).GetMethod(
                nameof(IHearing.ReceiveVoice),
                [typeof(string), typeof(IVoice), typeof(SpeechSegmentMetadata)])!.GetParameters().Select(parameter => parameter.ParameterType));
        _ = Assert.IsAssignableFrom<IPercept>(new SpeechPercept("completed", "source"));
    }

    /// <summary>Voice semantic identity remains canonical and identifiable.</summary>
    [Fact]
    public void VoiceContract_IsIdentifiableWithCanonicalVoiceType()
    {
        Assert.True(typeof(AlleyCat.Core.IIdentifiable).IsAssignableFrom(typeof(IVoice)));
        AlleyCat.Core.IIdentifiable voice = new TestVoice("external");
        Assert.Equal("voice", voice.Type);
    }

    /// <summary>Voice lifecycle events remain textless metadata transitions with interface defaults for fakes.</summary>
    [Fact]
    public void VoiceContract_DeclaresTextlessSegmentLifecycleEvents()
    {
        Assert.Equal(
            typeof(Action<IVoice, SpeechSegmentMetadata>),
            typeof(IVoice).GetEvent(nameof(IVoice.SpeechSegmentStarted))!.EventHandlerType);
        Assert.Equal(
            typeof(Action<IVoice, SpeechSegmentMetadata>),
            typeof(IVoice).GetEvent(nameof(IVoice.SpeechResumed))!.EventHandlerType);
        Assert.Equal(
            typeof(Action<IVoice, SpeechSegmentSettlement>),
            typeof(IVoice).GetEvent(nameof(IVoice.SpeechSegmentSettled))!.EventHandlerType);
    }

    private sealed class TestVoice(string id) : IVoice
    {
        public string Id { get; set; } = id;

        public Godot.Vector3 Origin => Godot.Vector3.Zero;

        public bool IsSpeaking => false;

#pragma warning disable CS0067
        public event Action<IVoice>? SpeechStarted;

        public event Action<IVoice>? SpeechEnded;
#pragma warning restore CS0067

        public void Speak(string speech)
        {
        }

        public ValueTask SpeakAsync(string speech, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask SpeakCancellableAsync(string speech, CancellationToken cancellationToken = default)
            => SpeakAsync(speech, cancellationToken);
    }
}
