using AlleyCat.Mind.Attention;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Mind.Attention;

/// <summary>Rest-relative witness-domain regression checks, independent of installed attainment.</summary>
public sealed class RestBoundedFoldTests
{
    /// <summary>Nonzero incoming rotation consumes the authored total range.</summary>
    [Fact]
    public void AnimatedHinge_ConsumesRestRange_NotAnotherFullCap()
    {
        float cap = Mathf.DegToRad(4f);
        Basis incoming = new(Vector3.Right, Mathf.DegToRad(2f));
        Assert.True(DownwardFoldGeometry.TryRestBoundedIncrement(
            Basis.Identity, incoming, Vector3.Up, cap, 1f, out float increment));
        Assert.InRange(Mathf.RadToDeg(increment), 1.999f, 2.001f);
        // Independent symptom discriminator: the old full 4-degree increment makes 6 degrees total.
        Vector3 oldDirection = incoming * (new Basis(Vector3.Right, cap) * Vector3.Up);
        Assert.True(Mathf.RadToDeg(Vector3.Up.AngleTo(oldDirection)) > 5.9f);
        for (int step = 0; step <= 32; step++)
        {
            Vector3 direction = incoming * (new Basis(Vector3.Right, increment * step / 32f) * Vector3.Up);
            double swing = Math.Atan2(Vector3.Up.Cross(direction).Length(), Vector3.Up.Dot(direction));
            Assert.True(swing <= cap + 1e-6d);
        }
    }

    /// <summary>Three-dimensional animation is evaluated on the actual outgoing rest vector.</summary>
    [Fact]
    public void NonSagittalAnimation_UsesComposedOutgoingVector()
    {
        Vector3 outgoing = new Vector3(0.02f, 1f, 0.2f).Normalized();
        Basis incoming = new Basis(Vector3.Up, Mathf.DegToRad(1f))
            * new Basis(Vector3.Right, Mathf.DegToRad(2f));
        float cap = Mathf.DegToRad(4f);
        Assert.True(DownwardFoldGeometry.TryRestBoundedIncrement(
            Basis.Identity, incoming, outgoing, cap, 1f, out float increment));
        for (int step = 0; step <= 32; step++)
        {
            Vector3 actual = incoming * (new Basis(Vector3.Right, increment * step / 32f) * outgoing);
            double swing = Math.Atan2(outgoing.Cross(actual).Length(), outgoing.Dot(actual));
            Assert.True(swing <= cap + 1e-6d);
        }
    }

    /// <summary>An already inadmissible upstream cannot seed the connected family.</summary>
    [Fact]
    public void AlreadyOutsideRestCone_IsUnsupported_NotSafe()
    {
        Assert.False(DownwardFoldGeometry.TryRestBoundedIncrement(
            Basis.Identity, new Basis(Vector3.Right, Mathf.DegToRad(5f)), Vector3.Up,
            Mathf.DegToRad(4f), 1f, out _));
    }
}
