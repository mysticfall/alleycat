using AlleyCat.Component;
using Godot;

namespace AlleyCat.Body.Hands;

/// <summary>
/// Component capability representing one hand that can expose and control a hand-pose override.
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
    /// Gets or sets the target hand pose resource; <see langword="null" /> clears the override.
    /// </summary>
    Resource? HandPose
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets the clamped rest-to-pose blend weight.
    /// </summary>
    float HandPoseWeight
    {
        get;
        set;
    }

    /// <summary>
    /// Gets the currently applied hand pose after transition state has settled.
    /// </summary>
    Resource? CurrentHandPose
    {
        get;
    }

    /// <summary>
    /// Sets or clears the hand pose, optionally overriding the weight and bypassing smoothing.
    /// </summary>
    void SetHandPose(Resource? pose, float? weight = null, bool immediate = false);

    /// <summary>
    /// Clears the current hand pose override.
    /// </summary>
    void ClearHandPose(bool immediate = false);
}
