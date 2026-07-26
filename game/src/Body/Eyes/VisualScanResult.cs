namespace AlleyCat.Body.Eyes;

/// <summary>
/// Immutable visible-cue result for one scanned subject.
/// </summary>
public sealed class VisualScanResult
{
    /// <summary>
    /// Creates a scan result with the cues visible on <paramref name="subject"/>.
    /// </summary>
    public VisualScanResult(IVisualSubject subject, IReadOnlyList<VisualCue> visibleCues)
    {
        Subject = subject ?? throw new ArgumentNullException(nameof(subject));
        ArgumentNullException.ThrowIfNull(visibleCues);
        if (visibleCues.Count == 0)
        {
            throw new ArgumentException("A visual scan result must contain at least one visible cue.", nameof(visibleCues));
        }

        VisibleCues = Array.AsReadOnly(visibleCues.ToArray());
    }

    /// <summary>Gets the subject that owns the visible cues.</summary>
    public IVisualSubject Subject
    {
        get;
    }

    /// <summary>Gets the visible cues in their authored order.</summary>
    public IReadOnlyList<VisualCue> VisibleCues
    {
        get;
    }
}
