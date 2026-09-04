using AlleyCat.XR.HandTracking;
using Godot;
using Xunit;

namespace AlleyCat.Tests.XR;

/// <summary>
/// Unit coverage for the per-side optical wrist freeze cache (XR-002 TR5, TR20, TR25, AC4).
/// </summary>
public sealed class XRWristFreezeCacheTests
{
    /// <summary>
    /// Before any capture the cache must report no data and no lifecycle flags.
    /// </summary>
    [Fact]
    public void TryGetTransform_BeforeAnyCapture_ReturnsFalse()
    {
        XRWristFreezeCache cache = new();

        Assert.False(cache.EverCaptured);
        Assert.False(cache.IsFrozen);
        Assert.False(cache.TryGetTransform(out Transform3D _));
    }

    /// <summary>
    /// A captured finite world-space sample must be returned verbatim and clear the frozen state.
    /// </summary>
    [Fact]
    public void Capture_FiniteTransform_IsReturnedVerbatim()
    {
        XRWristFreezeCache cache = new();
        Transform3D sample = CreateTransform(1.0f, 2.0f, 3.0f);

        cache.Capture(sample);

        Assert.True(cache.EverCaptured);
        Assert.False(cache.IsFrozen);
        Assert.True(cache.TryGetTransform(out Transform3D transform));
        Assert.Equal(sample, transform);
    }

    /// <summary>
    /// Tracking loss must retain the last valid world-space transform without mutating it (XR-002 TR25).
    /// </summary>
    [Fact]
    public void MarkLost_AfterCapture_RetainsLastValidTransform()
    {
        XRWristFreezeCache cache = new();
        Transform3D first = CreateTransform(1.0f, 2.0f, 3.0f);
        Transform3D last = CreateTransform(4.0f, 5.0f, 6.0f);
        cache.Capture(first);
        cache.Capture(last);

        cache.MarkLost();

        Assert.True(cache.EverCaptured);
        Assert.True(cache.IsFrozen);
        Assert.True(cache.TryGetTransform(out Transform3D transform));
        Assert.Equal(last, transform);
    }

    /// <summary>
    /// Loss before any capture must not report retained data.
    /// </summary>
    [Fact]
    public void MarkLost_BeforeAnyCapture_ReportsNoData()
    {
        XRWristFreezeCache cache = new();

        cache.MarkLost();

        Assert.False(cache.EverCaptured);
        Assert.False(cache.IsFrozen);
        Assert.False(cache.TryGetTransform(out Transform3D _));
    }

    /// <summary>
    /// Recapturing after loss must replace the frozen transform and clear the frozen state.
    /// </summary>
    [Fact]
    public void Capture_AfterLoss_ReplacesFrozenTransformAndClearsFrozen()
    {
        XRWristFreezeCache cache = new();
        cache.Capture(CreateTransform(1.0f, 2.0f, 3.0f));
        cache.MarkLost();

        Transform3D recaptured = CreateTransform(7.0f, 8.0f, 9.0f);
        cache.Capture(recaptured);

        Assert.True(cache.EverCaptured);
        Assert.False(cache.IsFrozen);
        Assert.True(cache.TryGetTransform(out Transform3D transform));
        Assert.Equal(recaptured, transform);
    }

    /// <summary>
    /// Non-finite samples must be rejected and treated as loss, retaining the previous valid transform
    /// (XR-002 TR20).
    /// </summary>
    [Fact]
    public void Capture_NonFiniteTransform_IsRejectedAndRetainsPrevious()
    {
        XRWristFreezeCache cache = new();
        Transform3D valid = CreateTransform(1.0f, 2.0f, 3.0f);
        cache.Capture(valid);

        cache.Capture(new Transform3D(Basis.Identity, new Vector3(float.NaN, 0.0f, 0.0f)));

        Assert.True(cache.EverCaptured);
        Assert.True(cache.IsFrozen);
        Assert.True(cache.TryGetTransform(out Transform3D transform));
        Assert.Equal(valid, transform);
    }

    /// <summary>
    /// Reset must clear the retained transform and both lifecycle flags, ending the optical session.
    /// </summary>
    [Fact]
    public void Reset_ClearsSampleAndLifecycleFlags()
    {
        XRWristFreezeCache cache = new();
        cache.Capture(CreateTransform(1.0f, 2.0f, 3.0f));
        cache.MarkLost();

        cache.Reset();

        Assert.False(cache.EverCaptured);
        Assert.False(cache.IsFrozen);
        Assert.False(cache.TryGetTransform(out Transform3D _));
    }

    /// <summary>
    /// The ever-captured flag must be false initially, stay true through loss and recapture, and clear only on reset.
    /// </summary>
    [Fact]
    public void EverCaptured_FollowsSessionLifecycle()
    {
        XRWristFreezeCache cache = new();

        Assert.False(cache.EverCaptured);

        cache.Capture(CreateTransform(1.0f, 2.0f, 3.0f));
        Assert.True(cache.EverCaptured);

        cache.MarkLost();
        Assert.True(cache.EverCaptured);

        cache.Capture(CreateTransform(4.0f, 5.0f, 6.0f));
        Assert.True(cache.EverCaptured);

        cache.Reset();
        Assert.False(cache.EverCaptured);
    }

    private static Transform3D CreateTransform(float x, float y, float z)
        => new(Basis.Identity, new Vector3(x, y, z));
}
