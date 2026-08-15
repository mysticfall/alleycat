using AlleyCat.Core;

namespace AlleyCat.Vision;

/// <summary>
/// An identifiable visual subject with discoverable authored cues.
/// </summary>
public interface IVisualSubject : IIdentifiable, IProvidesVisualCues
{
}
