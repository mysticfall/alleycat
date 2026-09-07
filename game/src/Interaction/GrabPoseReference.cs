using AlleyCat.Rigging;
using AlleyCat.XR.HandTracking;
using Godot;

namespace AlleyCat.Interaction;

/// <summary>
/// Validated, instance-exact authored reference carried by an eligible grab candidate.
/// </summary>
/// <remarks>
/// Validation is cached only for the immutable animation instance and hand side. The descriptor is the shared
/// boundary between candidate eligibility, recognition, optical presentation, and AnimationTree playback; names and
/// paths are diagnostic data only and never participate in identity.
/// </remarks>
public sealed class GrabPoseReference
{
    private static readonly Dictionary<ulong, SideReferences> _referencesByAnimationInstance = [];

    private GrabPoseReference(Animation animation, LimbSide side, AuthoredHandPoseSideReference sampledReference)
    {
        Animation = animation;
        Side = side;
        SampledReference = sampledReference;
    }

    /// <summary>The exact authored animation instance. This is the reference identity.</summary>
    public Animation Animation
    {
        get;
    }

    /// <summary>The hand side for which the reference tracks were validated.</summary>
    public LimbSide Side
    {
        get;
    }

    /// <summary>The already validated destination-local finger reference used by recognition and presentation.</summary>
    public AuthoredHandPoseSideReference SampledReference
    {
        get;
    }

    /// <summary>
    /// Validates and resolves an animation instance for one candidate side. Pathless in-memory resources are valid:
    /// sampling is performed directly on <paramref name="animation"/> and never reloads
    /// <see cref="Resource.ResourcePath"/>.
    /// </summary>
    public static bool TryCreate(Animation animation, LimbSide side, out GrabPoseReference reference, out string error)
    {
        ArgumentNullException.ThrowIfNull(animation);

        ulong instanceId = animation.GetInstanceId();
        if (!_referencesByAnimationInstance.TryGetValue(instanceId, out SideReferences? references))
        {
            references = new SideReferences(animation);
            _referencesByAnimationInstance.Add(instanceId, references);
        }

        return references.TryGet(side, out reference, out error);
    }

    private sealed class SideReferences(Animation animation)
    {
        private readonly Animation _animation = animation;
        private readonly GrabPoseReference?[] _references = new GrabPoseReference?[2];
        private readonly string?[] _errors = new string?[2];

        public bool TryGet(LimbSide side, out GrabPoseReference reference, out string error)
        {
            int index = (int)side;
            if (_references[index] is { } validated)
            {
                reference = validated;
                error = string.Empty;
                return true;
            }

            if (_errors[index] is { } validationError)
            {
                reference = null!;
                error = validationError;
                return false;
            }

            if (AuthoredHandPoseReferenceSampler.TrySample(_animation, side, out AuthoredHandPoseSideReference sampled, out error))
            {
                reference = new GrabPoseReference(_animation, side, sampled);
                _references[index] = reference;
                return true;
            }

            _errors[index] = error;
            reference = null!;
            return false;
        }
    }
}
