using System.Text.RegularExpressions;

namespace AlleyCat.IntegrationTests.Support.AI;

/// <summary>
/// Trace-level sanitisation: redacts credential-shaped values and bounds text length so trace records and
/// serialised artefacts never carry credentials. The redaction patterns mirror the assertion-output rules in
/// <see cref="LiveLLMEvaluation.Sanitise" />; traces use a larger length bound because they are inspectable
/// evidence files rather than assertion output.
/// </summary>
internal static class LiveLLMTraceSanitiser
{
    private const int MaximumTextLength = 4000;

    private static readonly Regex _credentialAssignmentPattern = new(
        @"(?i)\b(api[-_ ]?key|apikey|authorization|bearer|token|password|passwd|secret|credential)s?\b\s*[:=]\s*\S+",
        RegexOptions.Compiled);

    private static readonly Regex _credentialUrlPattern = new(
        @"[a-z][a-z0-9+.-]*://[^\s/@:]+:[^\s/@]+@",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex _opaqueTokenPattern = new(@"[A-Za-z0-9_+/=-]{24,}", RegexOptions.Compiled);

    /// <summary>
    /// Redacts credential-shaped values and bounds length so arbitrary model- or provider-originated text can
    /// be stored in traces safely.
    /// </summary>
    public static string SanitiseText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        // Redact before truncating: truncating first could leave a split credential tail too short for the
        // opaque-token pattern to match.
        string redacted = _credentialAssignmentPattern.Replace(text, "$1=[redacted]");
        redacted = _credentialUrlPattern.Replace(redacted, "[redacted]://");
        redacted = _opaqueTokenPattern.Replace(redacted, "[redacted]");
        return redacted.Length <= MaximumTextLength
            ? redacted
            : $"{redacted[..MaximumTextLength]}[truncated]";
    }

    /// <summary>
    /// Reports whether the text carries a credential-shaped value <see cref="SanitiseText" /> would redact —
    /// an assignment to a credential-like key, an embedded credential URL, or an opaque token — so callers can
    /// assert that emitted artefacts are credential-free without altering them.
    /// </summary>
    internal static bool ContainsCredentialShape(string? text)
        => !string.IsNullOrWhiteSpace(text)
            && (_credentialAssignmentPattern.IsMatch(text)
                || _credentialUrlPattern.IsMatch(text)
                || _opaqueTokenPattern.IsMatch(text));
}
