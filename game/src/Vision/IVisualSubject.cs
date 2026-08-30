using AlleyCat.Core;

namespace AlleyCat.Vision;

/// <summary>
/// An identifiable visual subject with discoverable authored cues and a world-space transform.
/// </summary>
public interface IVisualSubject : IIdentifiable, IProvidesVisualCues, ISpatial
{
}
