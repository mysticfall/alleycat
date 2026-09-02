namespace AlleyCat.Speech;

/// <summary>Immutable, generic identity for one completed automatic speech segment.</summary>
public sealed record SpeechSegmentMetadata
{
    /// <summary>Creates immutable identity metadata for one completed automatic speech segment.</summary>
    public SpeechSegmentMetadata(string speechGroupID, int segmentIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(speechGroupID);
        ArgumentOutOfRangeException.ThrowIfNegative(segmentIndex);

        SpeechGroupID = speechGroupID;
        SegmentIndex = segmentIndex;
        Continued = segmentIndex > 0;
    }

    /// <summary>Gets the stable opaque identity of the automatic speech group.</summary>
    public string SpeechGroupID
    {
        get;
    }

    /// <summary>Gets this segment's zero-based index within its speech group.</summary>
    public int SegmentIndex
    {
        get;
    }

    /// <summary>Gets whether this segment follows an earlier segment in its group.</summary>
    public bool Continued
    {
        get;
    }
}
