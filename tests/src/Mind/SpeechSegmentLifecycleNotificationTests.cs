using System.Reflection;
using AlleyCat.Mind;
using AlleyCat.Speech;
using Xunit;

namespace AlleyCat.Tests.Mind;

/// <summary>
/// Contract coverage for the textless speech-segment lifecycle notification boundary.
/// </summary>
public sealed class SpeechSegmentLifecycleNotificationTests
{
    /// <summary>The onset transition is a first-class textless lifecycle cue alongside resume and the terminals.</summary>
    [Fact]
    public void Transition_StartedIsDistinctFromResumeAndTerminals()
    {
        SpeechSegmentLifecycleTransition started = SpeechSegmentLifecycleTransition.Started;

        Assert.NotEqual(SpeechSegmentLifecycleTransition.Resumed, started);
        Assert.NotEqual(SpeechSegmentLifecycleTransition.Blank, started);
        Assert.NotEqual(SpeechSegmentLifecycleTransition.Failed, started);
        Assert.NotEqual(SpeechSegmentLifecycleTransition.Abandoned, started);
    }

    /// <summary>Every non-published terminal settlement kind maps onto a same-named lifecycle transition.</summary>
    [Fact]
    public void TerminalSettlementKinds_MapOntoLifecycleTransitionsExceptPublished()
    {
        foreach (SpeechSegmentSettlementKind kind in Enum.GetValues<SpeechSegmentSettlementKind>())
        {
            bool mapped = Enum.TryParse<SpeechSegmentLifecycleTransition>(kind.ToString(), out _);

            Assert.Equal(kind is not SpeechSegmentSettlementKind.Published, mapped);
        }
    }

    /// <summary>The notification carries identity and a transition only, remaining immutable and textless.</summary>
    [Fact]
    public void Notification_CarriesIdentityAndTransitionWithoutTranscriptText()
    {
        SpeechSegmentMetadata metadata = new("group", 0);
        SpeechSegmentLifecycleNotification notification = new(
            "voice-1",
            metadata,
            SpeechSegmentLifecycleTransition.Started);

        Assert.Equal("voice-1", notification.SourceVoiceID);
        Assert.Same(metadata, notification.Metadata);
        Assert.Equal(SpeechSegmentLifecycleTransition.Started, notification.Transition);
        Assert.DoesNotContain(
            typeof(SpeechSegmentLifecycleNotification).GetProperties(),
            property => property.PropertyType == typeof(string)
                && !string.Equals(property.Name, nameof(SpeechSegmentLifecycleNotification.SourceVoiceID), StringComparison.Ordinal));
    }

    /// <summary>The transition enum exposes exactly the onset, resume, and terminal surface.</summary>
    [Fact]
    public void Transition_DeclaresOnlyTheOnsetResumeAndTerminalMembers()
    {
        Assert.Equal(
            [
                nameof(SpeechSegmentLifecycleTransition.Started),
                nameof(SpeechSegmentLifecycleTransition.Resumed),
                nameof(SpeechSegmentLifecycleTransition.Blank),
                nameof(SpeechSegmentLifecycleTransition.Failed),
                nameof(SpeechSegmentLifecycleTransition.Abandoned),
            ],
            Enum.GetNames<SpeechSegmentLifecycleTransition>());
    }

    /// <summary>Positional construction order stays stable for the notification record surface.</summary>
    [Fact]
    public void Notification_ConstructorSignatureRemainsPositionalIdentityThenTransition()
    {
        ParameterInfo[] parameters = typeof(SpeechSegmentLifecycleNotification).GetConstructors().Single().GetParameters();

        Assert.Equal(
            [typeof(string), typeof(SpeechSegmentMetadata), typeof(SpeechSegmentLifecycleTransition)],
            parameters.Select(parameter => parameter.ParameterType));
    }
}
