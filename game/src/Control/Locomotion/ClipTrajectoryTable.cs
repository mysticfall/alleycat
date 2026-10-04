using System.Collections.Immutable;
using Godot;

namespace AlleyCat.Control.Locomotion;

/// <summary>
/// Runtime trajectory table of one package library bound to one rig: every catalogue clip's
/// <see cref="ClipTrajectory"/> at the rig's <see cref="Skeleton3D.MotionScale"/>, plus any uncatalogued clips (Idle)
/// the rig's AnimationTree joins to them. Uncatalogued clips carry no capability and are looked up by role only.
/// </summary>
public sealed class ClipTrajectoryTable
{
    /// <summary>Largest source-unit endpoint displacement mismatch against the catalogue, in metres.</summary>
    public const float CatalogueEndpointTolerance = 0.0001f;

    /// <summary>Largest endpoint yaw mismatch against the catalogue, in radians (0.01°).</summary>
    public static readonly float CatalogueYawTolerance = Mathf.DegToRad(0.01f);

    private readonly ImmutableDictionary<string, ClipTrajectory> _byRole;
    private readonly ImmutableDictionary<StandingLocomotionCapability, ClipTrajectory> _byCapability;

    /// <summary>
    /// Binds trajectories to a rig's motion scale. Root data is re-bound to <paramref name="motionScale"/>; ball data
    /// must already have been evaluated on a rig with that scale.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Roles or capabilities repeat, ball data was evaluated at another motion scale, or the scale is not finite and
    /// positive.
    /// </exception>
    public ClipTrajectoryTable(float motionScale, IEnumerable<ClipTrajectory> trajectories)
    {
        ArgumentNullException.ThrowIfNull(trajectories);
        if (!float.IsFinite(motionScale) || motionScale <= 0f)
        {
            throw new ArgumentException("Motion scale must be finite and positive.", nameof(motionScale));
        }

        ImmutableDictionary<string, ClipTrajectory>.Builder byRole =
            ImmutableDictionary.CreateBuilder<string, ClipTrajectory>(StringComparer.Ordinal);
        ImmutableDictionary<StandingLocomotionCapability, ClipTrajectory>.Builder byCapability =
            ImmutableDictionary.CreateBuilder<StandingLocomotionCapability, ClipTrajectory>();
        foreach (ClipTrajectory trajectory in trajectories)
        {
            // Ball samples are runtime data of the rig they were evaluated on; they are never rescaled to another rig.
            if (Math.Abs(trajectory.BallMotionScale - motionScale) > 1e-6f * motionScale)
            {
                throw new ArgumentException(
                    FormattableString.Invariant(
                        $"Trajectory '{trajectory.Role}' ball samples were evaluated at motion scale {trajectory.BallMotionScale}, not the table's {motionScale}."),
                    nameof(trajectories));
            }

            ClipTrajectory scaled = trajectory.WithMotionScale(motionScale);
            if (!byRole.TryAdd(scaled.Role, scaled)
                || (scaled.Capability is { } capability && !byCapability.TryAdd(capability, scaled)))
            {
                throw new ArgumentException(
                    $"Trajectory role '{scaled.Role}' or capability '{scaled.Capability}' is declared more than once.",
                    nameof(trajectories));
            }
        }

        MotionScale = motionScale;
        _byRole = byRole.ToImmutable();
        _byCapability = byCapability.ToImmutable();
    }

    /// <summary>Bound skeleton motion scale applied by every query.</summary>
    public float MotionScale
    {
        get;
    }

    /// <summary>Trajectories by package role.</summary>
    public IReadOnlyDictionary<string, ClipTrajectory> Clips => _byRole;

    /// <summary>Trajectory of a package role.</summary>
    public ClipTrajectory this[string role] => _byRole[role];

    /// <summary>Trajectory of the clip declaring a capability.</summary>
    public ClipTrajectory this[StandingLocomotionCapability capability] => _byCapability[capability];

    /// <summary>Looks up the trajectory of a package role.</summary>
    public bool TryGetClip(string role, out ClipTrajectory? trajectory) => _byRole.TryGetValue(role, out trajectory);

    /// <summary>
    /// Requires one trajectory per catalogue clip, with matching identity, capability and duration, and a source-unit
    /// endpoint that matches the catalogue's endpoint displacement and yaw within
    /// <see cref="CatalogueEndpointTolerance"/> and <see cref="CatalogueYawTolerance"/>. Uncatalogued clips (without
    /// a capability) are not part of the comparison.
    /// </summary>
    /// <exception cref="InvalidDataException">The table and catalogue disagree.</exception>
    public void ValidateAgainst(StandingLocomotionCatalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ValidateAgainst(catalogue.Clips.Values);
    }

    /// <inheritdoc cref="ValidateAgainst(StandingLocomotionCatalogue)"/>
    internal void ValidateAgainst(IEnumerable<StandingLocomotionClip> clips)
    {
        int count = 0;
        foreach (StandingLocomotionClip clip in clips)
        {
            count++;
            if (!_byRole.TryGetValue(clip.Role, out ClipTrajectory? trajectory))
            {
                throw new InvalidDataException($"Trajectory table has no clip for catalogue role '{clip.Role}'.");
            }

            if (trajectory.Identity != clip.Identity
                || trajectory.Capability != clip.Capability
                || Math.Abs(trajectory.DurationSeconds - clip.DurationSeconds) > 1e-4d)
            {
                throw new InvalidDataException(
                    $"Trajectory '{trajectory.Identity}' does not match catalogue clip '{clip.Identity}' identity, capability or duration.");
            }

            ClipTrajectorySlice endpoint = trajectory.Endpoint;
            Vector2 sourceEndpoint = endpoint.Planar / MotionScale;
            // The catalogue endpoint uses actor-local Godot axes (+X right, -Z forward).
            var catalogueEndpoint = new Vector2(clip.EndpointRootDisplacement.X, -clip.EndpointRootDisplacement.Z);
            float planarError = (sourceEndpoint - catalogueEndpoint).Length();
            float yawError = Mathf.Abs(endpoint.Yaw - clip.EndpointYaw);
            if (planarError > CatalogueEndpointTolerance || yawError > CatalogueYawTolerance)
            {
                throw new InvalidDataException(
                    FormattableString.Invariant(
                        $"Trajectory '{clip.Identity}' endpoint differs from the catalogue by {planarError * 1000f:0.###} mm / {Mathf.RadToDeg(yawError):0.####}°."));
            }
        }

        if (count != _byCapability.Count)
        {
            throw new InvalidDataException("Trajectory table declares clips the catalogue does not.");
        }
    }
}
