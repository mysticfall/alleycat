using AlleyCat.Core;
using AlleyCat.Interaction;

namespace AlleyCat.Body.Hands;

/// <summary>
/// Component capability representing one hand's grab lifecycle.
/// </summary>
public interface IHand : IComponent
{
    /// <summary>
    /// Gets the limb side represented by this hand component.
    /// </summary>
    LimbSide Side
    {
        get;
    }

    /// <summary>
    /// Gets the object currently held by this hand, or <see langword="null" /> when empty.
    /// </summary>
    IGrabbable? CurrentGrabbed
    {
        get;
    }

    /// <summary>
    /// Attempts to discover and grab a nearby object.
    /// </summary>
    /// <returns>The grabbed object, or <see langword="null" /> when no valid object can be grabbed.</returns>
    IGrabbable? Grab();

    /// <summary>
    /// Releases the currently held object, if any.
    /// </summary>
    void Release();
}
