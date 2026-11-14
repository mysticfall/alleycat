using AlleyCat.Sense;

namespace AlleyCat.Speech;

/// <summary>Immutable speech transport snapshot without embodied or semantic references.</summary>
public sealed record SpeechPercept : IPercept
{
    /// <summary>
    /// Creates a speech snapshot from accepted transport data.
    /// </summary>
    /// <param name="content">Completed speech text.</param>
    /// <param name="sourceVoiceID">Raw local source voice identifier captured at publication.</param>
    /// <param name="metadata">Optional automatic speech-group segment identity.</param>
    /// <param name="declaredRecipients">
    /// Canonical character FullIds the speaker explicitly declared as addressees, or null/empty when the speaker
    /// left the addressees unspecified. Recipients are declaration metadata only: they never restrict which
    /// hearings receive this percept.
    /// </param>
    public SpeechPercept(
        string content,
        string sourceVoiceID,
        SpeechSegmentMetadata? metadata = null,
        IReadOnlyList<string>? declaredRecipients = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        Content = content.Trim();
        SourceVoiceID = sourceVoiceID ?? throw new ArgumentNullException(nameof(sourceVoiceID));
        SpeechGroupID = metadata?.SpeechGroupID;
        SegmentIndex = metadata?.SegmentIndex ?? 0;
        Continued = metadata?.Continued ?? false;
        DeclaredRecipients = declaredRecipients is { Count: > 0 } ? [.. declaredRecipients] : [];
    }

    /// <summary>Gets the trimmed speech content.</summary>
    public string Content
    {
        get;
    }

    /// <summary>Gets the raw local source voice identifier captured at publication.</summary>
    public string SourceVoiceID
    {
        get;
    }

    /// <summary>Gets the optional automatic speech-group identity.</summary>
    public string? SpeechGroupID
    {
        get;
    }

    /// <summary>Gets the segment index, defaulting to zero for ungrouped speech.</summary>
    public int SegmentIndex
    {
        get;
    }

    /// <summary>Gets whether this is a continued automatic segment.</summary>
    public bool Continued
    {
        get;
    }

    /// <summary>
    /// Gets the immutable ordered set of canonical character FullIds the speaker explicitly declared as
    /// addressees, or an empty set when the speaker left the addressees unspecified.
    /// </summary>
    public IReadOnlyList<string> DeclaredRecipients
    {
        get;
    }
}
