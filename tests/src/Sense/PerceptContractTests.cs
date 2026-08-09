using System.Collections;
using System.Reflection;
using AlleyCat.Body.Eyes;
using AlleyCat.Body.Voice;
using AlleyCat.Sense;
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
            || (property.PropertyType.IsClass && property.PropertyType.Namespace?.StartsWith("AlleyCat.Body", StringComparison.Ordinal) == true));
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
        Assert.DoesNotContain(typeof(IEyes).GetMethods(), method => method.Name == "Scan");
        Assert.True(typeof(ISense).IsAssignableFrom(typeof(EyesBehaviour)));
        Assert.True(typeof(ISense).IsAssignableFrom(typeof(Hearing)));
    }

    /// <summary>Voice semantic identity remains canonical and identifiable.</summary>
    [Fact]
    public void VoiceContract_IsIdentifiableWithCanonicalVoiceType()
    {
        Assert.True(typeof(AlleyCat.Core.IIdentifiable).IsAssignableFrom(typeof(IVoice)));
        AlleyCat.Core.IIdentifiable voice = new TestVoice("external");
        Assert.Equal("voice", voice.Type);
    }

    private sealed class TestVoice(string id) : IVoice
    {
        public string Id { get; set; } = id;

        public Godot.Vector3 Origin => Godot.Vector3.Zero;

        public void Speak(string speech)
        {
        }

        public ValueTask SpeakAsync(string speech, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
