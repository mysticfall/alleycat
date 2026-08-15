using AlleyCat.Sense;

namespace AlleyCat.Speech;

/// <summary>Immutable speech transport snapshot without embodied or semantic references.</summary>
public sealed record SpeechPercept : IPercept
{
    /// <summary>Creates a speech snapshot from accepted transport data.</summary>
    public SpeechPercept(string content, string sourceVoiceID)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        Content = content.Trim();
        SourceVoiceID = sourceVoiceID ?? throw new ArgumentNullException(nameof(sourceVoiceID));
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
}
