using AlleyCat.Sense;
namespace AlleyCat.Vision;

/// <summary>
/// Component capability representing paired eye look and blink control.
/// </summary>
public interface IVision : ISense<IVisualPercept>
{
    /// <summary>
    /// Gets or sets the optional target node the eyes should look towards.
    /// </summary>
    VisualCue? LookTarget
    {
        get;
        set;
    }

    /// <summary>
    /// Sets the optional target node the eyes should look towards.
    /// </summary>
    void SetLookTarget(VisualCue? target);

    /// <summary>
    /// Clears the current directed look target.
    /// </summary>
    void ClearLookTarget();
}
