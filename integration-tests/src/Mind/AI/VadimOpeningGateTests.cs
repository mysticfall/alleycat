using AlleyCat.TestFramework;
using Xunit;

namespace AlleyCat.IntegrationTests.Mind.AI;

/// <summary>
/// Deterministic checks of the fail-closed opening gate: the corrected experiment's precondition is verified
/// only from accepted, settled <c>speak</c> content, and only an explicit full-name question permits delivering
/// the scripted player line (plan: Corrected Experiment Design).
/// </summary>
[Headless]
public sealed class VadimOpeningGateTests
{
    /// <summary>The historical baseline opening — an imperative full-name request — satisfies the precondition.</summary>
    [Fact]
    public void Classify_ExplicitFullNameRequests_PassThePrecondition()
    {
        foreach (string question in new[]
        {
            // The historical baseline opening.
            "Ally. The recording is running. Please state your full name for the record.",
            "What is your full name?",
            "May I have your full name for the file?",
            "State your full name.",
            "Full name?",

            // The question or request must be in the name's own sentence, not merely elsewhere in the opening.
            "Good morning. What is your full name, please?",
            "The recording is running. Who else is here? State your full name for the record.",
        })
        {
            Assert.Equal(VadimOpeningKind.FullNameQuestion, VadimOpeningGate.Classify(question));
        }
    }

    /// <summary>
    /// A question mark in an unrelated sentence does not qualify a declarative name mention: the interrogative or
    /// imperative-request form must occur in the sentence that references the name.
    /// </summary>
    [Fact]
    public void Classify_DeclarativeNameMentionWithUnrelatedQuestionElsewhere_IsAmbiguous()
    {
        foreach (string opening in new[]
        {
            // An unrelated interrogative plus a declarative full-name mention is not a full-name question.
            "Who is out there? My full name is Vadim Petrov.",
            "My full name is Vadim Petrov. Can we begin?",

            // The generic-name variant of the same failure mode fails closed identically.
            "Who is out there? My name is Vadim Petrov.",
        })
        {
            Assert.Equal(VadimOpeningKind.AmbiguousNameMention, VadimOpeningGate.Classify(opening));
        }
    }

    /// <summary>The prior candidate trial's actual live opening never asked for the name: a non-question.</summary>
    [Fact]
    public void Classify_OpeningsWithoutNameContent_AreNonQuestions()
    {
        foreach (string opening in new[]
        {
            // The prior trial's opening, which established no name-question precondition.
            "Good morning, Ally. Take a seat. The session is recorded, so please answer only when the question "
                + "has been asked.",
            "Sit down. The recording is running.",
            "We will begin shortly.",
        })
        {
            Assert.Equal(VadimOpeningKind.NonQuestion, VadimOpeningGate.Classify(opening));
        }
    }

    /// <summary>Name requests that never ask for the full name specifically do not satisfy the precondition.</summary>
    [Fact]
    public void Classify_NameRequestsWithoutExplicitFullName_AreGenericNameRequests()
    {
        foreach (string question in new[]
        {
            "What's your name?",
            "Tell me your name.",
            "State your name for the record.",
        })
        {
            Assert.Equal(VadimOpeningKind.GenericNameRequest, VadimOpeningGate.Classify(question));
        }
    }

    /// <summary>Name mentions without a determinate request stay ambiguous and fail closed.</summary>
    [Fact]
    public void Classify_NameMentionsWithoutRequestForm_AreAmbiguous()
    {
        foreach (string opening in new[]
        {
            "My name is Vadim. We will begin.",
            "I have your full name right here in the file.",
            "Your full name, once you are ready.",

            // A full-name mention in declarative form is not a request, however promising it sounds.
            "Your full name will be verified against the file.",
            "Names are such interesting things.",
        })
        {
            Assert.Equal(VadimOpeningKind.AmbiguousNameMention, VadimOpeningGate.Classify(opening));
        }
    }

    /// <summary>Missing or blank accepted speech is no accepted speech, never a passing precondition.</summary>
    [Fact]
    public void Classify_MissingSpeech_HasNoAcceptedSpeechKind()
    {
        Assert.Equal(VadimOpeningKind.NoAcceptedSpeech, VadimOpeningGate.Classify(null));
        Assert.Equal(VadimOpeningKind.NoAcceptedSpeech, VadimOpeningGate.Classify(string.Empty));
        Assert.Equal(VadimOpeningKind.NoAcceptedSpeech, VadimOpeningGate.Classify("   "));
    }
}
