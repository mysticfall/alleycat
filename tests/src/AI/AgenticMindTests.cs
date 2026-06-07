using AlleyCat.AI.Observation;
using Xunit;

namespace AlleyCat.Tests.AI;

/// <summary>
/// Unit coverage for observation contracts consumed by agentic minds.
/// </summary>
public sealed class AgenticMindTests
{
    /// <summary>
    /// Speech observations own their default scheduling significance without Mind-specific configuration.
    /// </summary>
    [Fact]
    public void SpeechObservation_DefaultWeight_IsInherentToObservationType()
    {
        SpeechObservation observation = new("player", "hello");

        Assert.Equal(1f, observation.Weight);
    }

    /// <summary>
    /// Speech observations own agent prompt formatting so the runtime does not type-switch on observation subtypes.
    /// </summary>
    [Fact]
    public void SpeechObservation_ToPromptString_DescribesSpeakerAndContent()
    {
        Observation observation = new SpeechObservation("player", "hello");

        Assert.Equal("Speech from player: hello", observation.ToPromptString());
    }
}
