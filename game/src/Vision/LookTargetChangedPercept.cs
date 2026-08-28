namespace AlleyCat.Vision;

/// <summary>Immutable snapshot of one effective visual focus transition.</summary>
public sealed record LookTargetChangedPercept : IVisualPercept
{
    /// <summary>Creates a focus transition retaining exact cue identity.</summary>
    public LookTargetChangedPercept(VisualCue? previous, VisualCue? current)
    {
        Previous = previous;
        Current = current;
    }

    /// <summary>Gets the previously focused cue, or null when focus was clear.</summary>
    public VisualCue? Previous
    {
        get;
    }

    /// <summary>Gets the newly focused cue, or null when focus was cleared.</summary>
    public VisualCue? Current
    {
        get;
    }
}
