namespace AlleyCat.Speech;

/// <summary>Terminal outcome for one automatic speech segment.</summary>
public enum SpeechSegmentSettlementKind
{
    /// <summary>The segment's nonblank transcript was published through hearing.</summary>
    Published,

    /// <summary>The segment completed with no transcript to publish.</summary>
    Blank,

    /// <summary>The segment's transcription failed.</summary>
    Failed,

    /// <summary>The segment was abandoned before an outcome could be published.</summary>
    Abandoned,
}

/// <summary>Immutable, textless terminal outcome for one automatic speech segment.</summary>
public sealed record SpeechSegmentSettlement
{
    /// <summary>Initialises a terminal segment settlement.</summary>
    /// <param name="metadata">Stable identity metadata for the settled segment.</param>
    /// <param name="kind">The terminal outcome kind.</param>
    public SpeechSegmentSettlement(SpeechSegmentMetadata metadata, SpeechSegmentSettlementKind kind)
    {
        Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        Kind = kind;
    }

    /// <summary>Gets the stable identity metadata for the settled segment.</summary>
    public SpeechSegmentMetadata Metadata
    {
        get;
    }

    /// <summary>Gets the terminal outcome kind.</summary>
    public SpeechSegmentSettlementKind Kind
    {
        get;
    }
}
