using Godot;

#pragma warning disable CS1591

namespace AlleyCat.Navigation;

public readonly record struct NPCRootTrajectoryKey(float Time, Vector2 Position);

/// <summary>Builds a C2 runtime sampling of packaged Root response without changing package content.</summary>
public static class NPCRootTrajectory
{
    public static NPCRootTrajectoryKey[] Resample(
        ReadOnlySpan<float> times,
        ReadOnlySpan<Vector2> positions,
        ReadOnlySpan<Vector2> velocities,
        float duration,
        float framesPerSecond,
        bool cyclic)
    {
        if (times.Length < 2 || positions.Length != times.Length || velocities.Length != times.Length)
        {
            throw new ArgumentException("Root trajectory response arrays must contain matching samples.");
        }
        if (!float.IsFinite(duration) || duration <= 0f || !float.IsFinite(framesPerSecond) || framesPerSecond <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Root trajectory duration and sampling rate must be positive.");
        }

        Vector2[] accelerations = BuildAccelerations(times, velocities, cyclic);
        int frameCount = Mathf.CeilToInt(duration * framesPerSecond);
        var result = new NPCRootTrajectoryKey[frameCount + 1];
        int interval = 0;
        for (int frame = 0; frame <= frameCount; frame++)
        {
            float time = frame == frameCount ? duration : Math.Min(frame / framesPerSecond, duration);
            while (interval + 1 < times.Length - 1 && time > times[interval + 1])
            {
                interval++;
            }
            result[frame] = new(time, EvaluateInterval(
                time,
                times[interval],
                times[interval + 1],
                positions[interval],
                positions[interval + 1],
                velocities[interval],
                velocities[interval + 1],
                accelerations[interval],
                accelerations[interval + 1]));
        }
        return result;
    }

    private static Vector2[] BuildAccelerations(
        ReadOnlySpan<float> times,
        ReadOnlySpan<Vector2> velocities,
        bool cyclic)
    {
        var result = new Vector2[times.Length];
        for (int index = 1; index < times.Length - 1; index++)
        {
            result[index] = (velocities[index + 1] - velocities[index - 1])
                / Math.Max(times[index + 1] - times[index - 1], float.Epsilon);
        }
        if (cyclic)
        {
            float seamDuration = Math.Max(
                times[1] - times[0] + (times[^1] - times[^2]),
                float.Epsilon);
            Vector2 seam = (velocities[1] - velocities[^2]) / seamDuration;
            result[0] = seam;
            result[^1] = seam;
        }
        else
        {
            result[0] = (velocities[1] - velocities[0]) / Math.Max(times[1] - times[0], float.Epsilon);
            result[^1] = (velocities[^1] - velocities[^2]) / Math.Max(times[^1] - times[^2], float.Epsilon);
        }
        return result;
    }

    private static Vector2 EvaluateInterval(
        float time,
        float startTime,
        float endTime,
        Vector2 startPosition,
        Vector2 endPosition,
        Vector2 startVelocity,
        Vector2 endVelocity,
        Vector2 startAcceleration,
        Vector2 endAcceleration)
    {
        float duration = Math.Max(endTime - startTime, float.Epsilon);
        float amount = Mathf.Clamp((time - startTime) / duration, 0f, 1f);
        float durationSquared = duration * duration;
        Vector2 positionRemainder = endPosition
            - startPosition
            - (startVelocity * duration)
            - (0.5f * startAcceleration * durationSquared);
        Vector2 velocityRemainder = ((endVelocity - startVelocity) * duration)
            - (startAcceleration * durationSquared);
        Vector2 accelerationRemainder = (endAcceleration - startAcceleration) * durationSquared;
        Vector2 c3 = (10f * positionRemainder) - (4f * velocityRemainder) + (0.5f * accelerationRemainder);
        Vector2 c4 = (-15f * positionRemainder) + (7f * velocityRemainder) - accelerationRemainder;
        Vector2 c5 = (6f * positionRemainder) - (3f * velocityRemainder) + (0.5f * accelerationRemainder);
        float amountSquared = amount * amount;
        return startPosition
            + (startVelocity * duration * amount)
            + (0.5f * startAcceleration * durationSquared * amountSquared)
            + (c3 * amountSquared * amount)
            + (c4 * amountSquared * amountSquared)
            + (c5 * amountSquared * amountSquared * amount);
    }
}
