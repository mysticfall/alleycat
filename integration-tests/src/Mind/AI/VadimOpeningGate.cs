using System.Text.RegularExpressions;

namespace AlleyCat.IntegrationTests.Mind.AI;

/// <summary>The fail-closed classification of the interrogation opening (plan: Corrected Experiment Design).</summary>
internal enum VadimOpeningKind
{
    /// <summary>The accepted, settled opening speech explicitly requests the subject's full name.</summary>
    FullNameQuestion,

    /// <summary>The opening requests a name, but not explicitly the subject's full name.</summary>
    GenericNameRequest,

    /// <summary>The opening contains no name-related content at all.</summary>
    NonQuestion,

    /// <summary>The opening is name-related, but not determinately a request for the subject's full name.</summary>
    AmbiguousNameMention,

    /// <summary>No accepted, settled opening speech exists to classify.</summary>
    NoAcceptedSpeech,
}

/// <summary>
/// Deterministic opening-precondition gate for the corrected Vadim interrogation experiment: it classifies the
/// accepted, settled opening <c>speak</c> content — never private reasoning, instructions, or assumed intent — and
/// fails closed, because only <see cref="VadimOpeningKind.FullNameQuestion" /> permits delivering the scripted
/// player line.
/// </summary>
/// <remarks>
/// <para>
/// The gate is deliberately conservative in the paid trial's favour of stopping: only an explicitly requested
/// <c>full name</c> in question or imperative-request form satisfies the precondition. A generic name request, a
/// name mention without a request, an unverifiable name-related opening, and an absent opening all record
/// <c>precondition missing</c> and consume the slot without injecting the name. No LLM call is involved.
/// </para>
/// <para>
/// Classification is purely textual: the interrogative or imperative-request form — a question mark or a request
/// verb (what/state/give/tell/say/provide/spell/repeat/confirm) governing the name phrase — must occur within the
/// same sentence as the name reference, so a declarative mention plus an unrelated question elsewhere in the
/// opening stays ambiguous. The historical baseline opening — <c>Please state your full name for the record.</c> —
/// classifies <see cref="VadimOpeningKind.FullNameQuestion" /> through its request verb; the prior candidate
/// trial's actual opening, which never asked for the name, classifies
/// <see cref="VadimOpeningKind.NonQuestion" /> and would have stopped the trial.
/// </para>
/// </remarks>
internal static partial class VadimOpeningGate
{
    [GeneratedRegex(@"\bfull\s+name\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FullNamePhrase();

    [GeneratedRegex(@"\bnames?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NamePhrase();

    [GeneratedRegex(
        @"\b(what|state|give|tell|say|provide|spell|repeat|confirm)\b[^.!?]*\bnames?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RequestVerbBeforeName();

    [GeneratedRegex(@"[^.!?]+[.!?]*", RegexOptions.CultureInvariant)]
    private static partial Regex Sentence();

    /// <summary>Classifies accepted, settled opening speech content; blank content is no accepted speech.</summary>
    /// <param name="acceptedSpeech">Joined accepted opening <c>speak</c> content, or null when none settled.</param>
    /// <returns>The conservative classification; only <see cref="VadimOpeningKind.FullNameQuestion" /> passes.</returns>
    public static VadimOpeningKind Classify(string? acceptedSpeech)
    {
        if (string.IsNullOrWhiteSpace(acceptedSpeech))
        {
            return VadimOpeningKind.NoAcceptedSpeech;
        }

        string normalised = Regex.Replace(
            acceptedSpeech.Replace('\u2019', '\'').Replace('\u2018', '\''),
            @"\s+",
            " ")
            .Trim();

        return FullNamePhrase().IsMatch(normalised)
            ? QuestionFormInSentence(normalised, FullNamePhrase())
                ? VadimOpeningKind.FullNameQuestion
                : VadimOpeningKind.AmbiguousNameMention
            : NamePhrase().IsMatch(normalised)
            ? QuestionFormInSentence(normalised, NamePhrase())
                ? VadimOpeningKind.GenericNameRequest
                : VadimOpeningKind.AmbiguousNameMention
            : VadimOpeningKind.NonQuestion;
    }

    /// <summary>
    /// Determines whether any single sentence both references the given name phrase and takes interrogative or
    /// imperative-request form: a question mark in that same sentence, or a request verb governing the name within
    /// it. A question mark in an unrelated sentence elsewhere in the opening does not qualify.
    /// </summary>
    private static bool QuestionFormInSentence(string normalised, Regex namePhrase)
    {
        foreach (Match sentence in Sentence().Matches(normalised))
        {
            string text = sentence.Value;
            if (namePhrase.IsMatch(text) && (text.Contains('?') || RequestVerbBeforeName().IsMatch(text)))
            {
                return true;
            }
        }

        return false;
    }
}
