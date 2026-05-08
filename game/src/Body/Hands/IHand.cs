using AlleyCat.Component;
using Godot;

namespace AlleyCat.Body.Hands;

/// <summary>
/// Component capability representing one hand that can expose and control a pose override.
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
    /// Gets or sets the target pose resource; <see langword="null" /> clears the override.
    /// </summary>
    Resource? Pose
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets the clamped rest-to-pose blend weight.
    /// </summary>
    float PoseWeight
    {
        get;
        set;
    }

    /// <summary>
    /// Gets the currently applied pose after transition state has settled.
    /// </summary>
    Resource? CurrentPose
    {
        get;
    }

    /// <summary>
    /// Sets or clears the pose, optionally overriding the weight and bypassing smoothing.
    /// </summary>
    void SetPose(Resource? pose, float? weight = null, bool immediate = false);

    /// <summary>
    /// Clears the current pose override.
    /// </summary>
    void ClearPose(bool immediate = false);
}
