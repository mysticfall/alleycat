using System.Numerics;

namespace AlleyCat.Control;

/// <summary>Horizontal curved intent blended from actual velocity over the comparison horizon.</summary>
public static class WalkingSteeringPrediction
{
    /// <summary>Finite positive ratio; zero is not a stop command. Invalid values use the trial default.</summary>
    public static float ValidBackwardRatio(float ratio) => float.IsFinite(ratio) && ratio > 0 ? Math.Min(ratio, 1) : 0.75f;

    /// <summary>Processed stick to bout-local X/Z: reduce only backward before any steering rotation.</summary>
    public static Vector2 RequestedTravel(Vector2 stick, float backwardRatio) =>
        new(stick.X, -stick.Y * (stick.Y < 0 ? ValidBackwardRatio(backwardRatio) : 1));

    /// <summary>Rotate an X/Z vector about +Y; negative yaw steers world-forward towards world-right.</summary>
    public static Vector2 Rotate(Vector2 value, double yaw) => Apply(value, Math.Cos(yaw), Math.Sin(yaw));

    /// <summary>Integrate the velocity blend through time, continuing the curve beyond the transition.</summary>
    public static Vector2 Offset(Vector2 actual, Vector2 requested, double rate, double horizon, double time)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(horizon);
        ArgumentOutOfRangeException.ThrowIfNegative(time);
        double q = Math.Min(time, horizon);
        (double c, double s) = Integral(q, rate, true);
        (double endC, double endS) = Integral(time, rate, false);
        (double startC, double startS) = Integral(q, rate, false);
        return (actual * (float)(q - (q * q / (2 * horizon))))
            + Apply(requested, (c / horizon) + endC - startC, (s / horizon) + endS - startS);
    }

    /// <summary>Instantaneous terminal velocity on the same trajectory as <see cref="Offset"/>.</summary>
    public static Vector2 Velocity(Vector2 actual, Vector2 requested, double rate, double horizon, double time)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(horizon);
        ArgumentOutOfRangeException.ThrowIfNegative(time);
        float weight = (float)Math.Min(time / horizon, 1);
        return (actual * (1 - weight)) + (Rotate(requested, rate * time) * weight);
    }

    private static Vector2 Apply(Vector2 value, double cosine, double sine) => new(
        (float)((cosine * value.X) + (sine * value.Y)), (float)((-sine * value.X) + (cosine * value.Y)));

    private static (double Cosine, double Sine) Integral(double time, double rate, bool weighted)
    {
        double x = rate * time;
        if (Math.Abs(x) < 0.01)
        {
            double x2 = x * x, x4 = x2 * x2;
            return weighted
                ? (time * time * (0.5 - (x2 / 8) + (x4 / 144)), time * time * x * ((1.0 / 3) - (x2 / 30) + (x4 / 840)))
                : (time * (1 - (x2 / 6) + (x4 / 120)), time * x * (0.5 - (x2 / 24) + (x4 / 720)));
        }
        double sin = Math.Sin(x), cos = Math.Cos(x);
        return weighted
            ? (((x * sin) + cos - 1) / (rate * rate), (sin - (x * cos)) / (rate * rate))
            : (sin / rate, (1 - cos) / rate);
    }
}
