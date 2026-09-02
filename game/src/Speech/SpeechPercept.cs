using AlleyCat.Sense;

namespace AlleyCat.Speech;

/// <summary>Immutable speech transport snapshot without embodied or semantic references.</summary>
public sealed record SpeechPercept : IPercept
{
    /// <summary>Creates a speech snapshot from accepted transport data.</summary>
    public SpeechPercept(string content, string sourceVoiceID, SpeechSegmentMetadata? metadata = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        Content = content.Trim();
        SourceVoiceID = sourceVoiceID ?? throw new ArgumentNullException(nameof(sourceVoiceID));
        SpeechGroupID = metadata?.SpeechGroupID;
        SegmentIndex = metadata?.SegmentIndex ?? 0;
        Continued = metadata?.Continued ?? false;
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
}
