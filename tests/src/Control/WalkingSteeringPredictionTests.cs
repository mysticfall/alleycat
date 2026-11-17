using System.Numerics;
using AlleyCat.Control;
using Xunit;

namespace AlleyCat.Tests.Control;

/// <summary>Independent quadrature and directional contracts for curved walking intent.</summary>
public sealed class WalkingSteeringPredictionTests
{
    /// <summary>Authorised component ratio, finite validation and rotation order, independently specified values.</summary>
    [Fact]
    public void BackwardRatio_ValidatesAndScalesOnlyBackwardBeforeRotation()
    {
        foreach (float invalid in new[] { 0f, -1f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            Assert.Equal(0.75f, WalkingSteeringPrediction.ValidBackwardRatio(invalid));
        }
        Assert.Equal(1, WalkingSteeringPrediction.ValidBackwardRatio(2));
        Assert.Equal(0.001f, WalkingSteeringPrediction.ValidBackwardRatio(0.001f));
        Assert.Equal(new Vector2(0, -0.5f), WalkingSteeringPrediction.RequestedTravel(new Vector2(0, 0.5f), 0.75f));
        Assert.Equal(new Vector2(0.5f, 0), WalkingSteeringPrediction.RequestedTravel(new Vector2(0.5f, 0), 0.75f));
        Assert.Equal(new Vector2(0, 0.375f), WalkingSteeringPrediction.RequestedTravel(new Vector2(0, -0.5f), 0.75f));
        Vector2 diagonal = WalkingSteeringPrediction.RequestedTravel(new Vector2(0.6f, -0.8f), 0.75f);
        Assert.Equal(new Vector2(0.6f, 0.6f), diagonal);
        Assert.True(diagonal.Length() <= 1);
        Vector2 rightTurn = WalkingSteeringPrediction.Rotate(diagonal, -Math.PI / 2);
        Assert.True(Vector2.Distance(new Vector2(-0.6f, 0.6f), rightTurn) < 1e-6f);
        foreach (Vector2 stick in new[] { new Vector2(0, 0.5f), new Vector2(0, -0.5f), new Vector2(0.6f, -0.8f) })
        {
            Assert.Equal(new Vector2(stick.X, -stick.Y), WalkingSteeringPrediction.RequestedTravel(stick, 1));
        }
    }

    /// <summary>The analytic integral agrees with independently sampled instantaneous velocity.</summary>
    [Theory]
    [InlineData(0, 0.2, 1.633333333333)]
    [InlineData(0, 2, 1.633333333333)]
    [InlineData(-0.26, 0.2, 1.633333333333)]
    [InlineData(-Math.PI / 3, 0.2, 1.633333333333)]
    [InlineData(Math.PI / 3, 2, 1.633333333333)]
    [InlineData(0.26, 2, 1.633333333333)]
    [InlineData(0.000001, 0.2, 0.2)]
    public void IntegralMatchesIndependentVelocityQuadrature(double rate, double horizon, double time)
    {
        var actual = new Vector2(0.1f, -0.2f);
        var requested = new Vector2(0, -0.4f);
        double x = 0, z = 0;
        const int count = 20000;
        double dt = time / count;
        for (int i = 0; i < count; i++)
        {
            double t = (i + 0.5) * dt, a = rate * t, weight = Math.Min(t / horizon, 1);
            x += (((1 - weight) * actual.X) + (weight * ((Math.Cos(a) * requested.X) + (Math.Sin(a) * requested.Y)))) * dt;
            z += (((1 - weight) * actual.Y) + (weight * ((-Math.Sin(a) * requested.X) + (Math.Cos(a) * requested.Y)))) * dt;
        }
        Vector2 result = WalkingSteeringPrediction.Offset(actual, requested, rate, horizon, time);
        Assert.True(Vector2.Distance(new Vector2((float)x, (float)z), result) < 1e-6f);
        if (rate == 0)
        {
            double q = Math.Min(horizon, time);
            Vector2 straight = (actual * (float)q) + ((requested - actual) * (float)(q * q / (2 * horizon))) + (requested * (float)(time - q));
            Assert.True(Vector2.Distance(straight, result) < 1e-6f);
        }
    }

    /// <summary>Signed rate, elapsed time and terminal samples preserve travel/heading separation.</summary>
    [Fact]
    public void SignRateAndTerminalSamplesFollowOneCurve()
    {
        var forward = new Vector2(0, -1);
        const double rate = -Math.PI / 6;
        Vector2 quarter = WalkingSteeringPrediction.Rotate(forward, rate * 3);
        Assert.True(Vector2.Distance(quarter, Vector2.UnitX) < 1e-6f);
        Assert.True(WalkingSteeringPrediction.Rotate(forward, -rate).X < 0);
        Assert.Equal(WalkingSteeringPrediction.Rotate(forward, rate), WalkingSteeringPrediction.Rotate(forward, rate * 0.5 * 2));
        double phase = 0;
        for (int i = 0; i < 180; i++)
        {
            phase += rate / 60;
        }
        Assert.True(Vector2.Distance(quarter, WalkingSteeringPrediction.Rotate(forward, phase)) < 1e-6f);
        Vector2 first = WalkingSteeringPrediction.Offset(forward, forward, rate, 0.2, 49.0 / 30);
        Vector2 second = WalkingSteeringPrediction.Offset(forward, forward, rate, 0.2, 50.0 / 30);
        Vector2 third = WalkingSteeringPrediction.Offset(forward, forward, rate, 0.2, 51.0 / 30);
        Assert.NotEqual(second - first, third - second);
        Vector2 velocity = WalkingSteeringPrediction.Velocity(forward, forward, rate, 0.2, 49.0 / 30);
        Assert.True(Vector2.Distance(velocity, WalkingSteeringPrediction.Rotate(forward, rate * 49 / 30)) < 1e-6f);
        // Strafe/backwards remain perpendicular/opposed to heading instead of becoming face-velocity.
        Vector2 facing = WalkingSteeringPrediction.Rotate(forward, phase);
        Assert.True(Math.Abs(Vector2.Dot(facing, WalkingSteeringPrediction.Rotate(Vector2.UnitX, phase))) < 1e-6f);
        Assert.True(Vector2.Dot(facing, WalkingSteeringPrediction.Rotate(-forward, phase)) < -0.99999f);
    }
}
