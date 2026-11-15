namespace AlleyCat.Vision;

/// <summary>
/// Vision components that maintain per-cue visibility knowledge from their own visual surveys.
/// </summary>
/// <remarks>
/// The snapshot is the most recent survey's result: it may lag the live frustum by up to one survey interval, and
/// reading it never runs a new geometric test. Consumers must treat it as the vision layer's current knowledge of
/// what is in view, nothing more.
/// </remarks>
internal interface IVisibleCueSurveySource
{
    /// <summary>Gets the most recent survey's per-subject visible cues, or null before the first survey.</summary>
    IReadOnlyList<VisualScanResult>? LastVisibleCueSurvey
    {
        get;
    }
}
