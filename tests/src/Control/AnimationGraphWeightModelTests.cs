using AlleyCat.Control.Locomotion;
using Godot;
using Xunit;

#pragma warning disable CS1591

namespace AlleyCat.Tests.Control;

/// <summary>
/// Pure tests for the runtime ANIM-002 graph weight models: state-machine crossfade weights, interpolated BlendSpace2D
/// weights (inside a triangle, on a vertex, and the closest-edge fallback outside the hull), and clip timeline mapping
/// (plain, stretched looping custom timeline, sliced non-looping custom timeline, and backward play).
/// </summary>
public sealed class AnimationGraphWeightModelTests
{
    private const float Tolerance = 1e-5f;

    [Fact]
    public void Crossfade_WithoutFading_GivesCurrentFullWeight()
        => Assert.Equal((1d, 0d), AnimationGraphWeightModel.CrossfadeWeights(false, 0d, 0d));

    [Fact]
    public void Crossfade_IsLinearInProgress_AndFloorsAtEngineEpsilon()
    {
        (double current, double from) = AnimationGraphWeightModel.CrossfadeWeights(true, 0.05d, 0.2d);
        Assert.Equal(0.25d, current, 9);
        Assert.Equal(0.75d, from, 9);

        (double start, double full) = AnimationGraphWeightModel.CrossfadeWeights(true, 0d, 0.2d);
        Assert.Equal(AnimationGraphWeightModel.EngineEpsilon, start);
        Assert.Equal(1d - AnimationGraphWeightModel.EngineEpsilon, full, 9);

        (double done, double rest) = AnimationGraphWeightModel.CrossfadeWeights(true, 0.3d, 0.2d);
        Assert.Equal(1d, done);
        Assert.Equal(AnimationGraphWeightModel.EngineEpsilon, rest);
    }

    [Fact]
    public void InterruptedCrossfade_IsDetectedOnlyWhenARunningFadeIsReplaced()
    {
        const double tick = 1d / 60d;

        // A fade 0.05 s into 0.2 s is replaced by a new transition from the previous current state.
        Assert.True(AnimationGraphWeightModel.InterruptsCrossfade(true, 0.05d, 0.2d, tick, currentChanged: true, fading: true, fadingFromIsPreviousCurrent: true));

        // The previous fade completes within this tick, so a transition started after it blends correctly.
        Assert.False(AnimationGraphWeightModel.InterruptsCrossfade(true, 0.2d - tick, 0.2d, tick, true, true, true));

        // No running fade, a fade that simply continues, or a teleport that clears fading are all modelled.
        Assert.False(AnimationGraphWeightModel.InterruptsCrossfade(false, 0d, 0d, tick, true, true, true));
        Assert.False(AnimationGraphWeightModel.InterruptsCrossfade(true, 0.05d, 0.2d, tick, false, true, false));
        Assert.False(AnimationGraphWeightModel.InterruptsCrossfade(true, 0.05d, 0.2d, tick, true, false, false));
    }

    [Fact]
    public void ChainedCrossfade_UnderNonUnitAdvanceDelta_IsJudgedByTheActualDelta()
    {
        const double tick = 1d / 60d;
        const double scaledDelta = 3d * tick;

        // At time scale 3 a fade 0.16 s into 0.2 s completes within the 0.05 s advance and the next transition chains
        // from its target: not an interruption, although one unscaled tick could not have finished it.
        Assert.False(AnimationGraphWeightModel.InterruptsCrossfade(true, 0.16d, 0.2d, scaledDelta, true, true, true));
        Assert.True(AnimationGraphWeightModel.InterruptsCrossfade(true, 0.16d, 0.2d, tick, true, true, true));

        // A fade that the scaled advance still cannot finish is an interruption; a slowed advance keeps flagging one
        // that a full tick would have finished.
        Assert.True(AnimationGraphWeightModel.InterruptsCrossfade(true, 0.1d, 0.2d, scaledDelta, true, true, true));
        Assert.True(AnimationGraphWeightModel.InterruptsCrossfade(true, 0.2d - tick, 0.2d, 0.5d * tick, true, true, true));
    }

    [Fact]
    public void BlendSpace_InsideTriangle_UsesBarycentricWeights()
    {
        var model = new BlendSpace2DWeightModel(
            [new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f)],
            [0, 1, 2, 1, 3, 2]);
        Span<float> weights = stackalloc float[4];
        model.Evaluate(new Vector2(0.25f, 0.25f), weights);
        Assert.Equal(0.5f, weights[0], Tolerance);
        Assert.Equal(0.25f, weights[1], Tolerance);
        Assert.Equal(0.25f, weights[2], Tolerance);
        Assert.Equal(0f, weights[3], Tolerance);

        model.Evaluate(new Vector2(1f, 1f), weights);
        Assert.Equal(1f, weights[3], Tolerance);
        Assert.Equal(0f, weights[0] + weights[1] + weights[2], Tolerance);
    }

    [Fact]
    public void BlendSpace_OutsideHull_UsesClosestEdge()
    {
        var model = new BlendSpace2DWeightModel(
            [new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f)],
            [0, 1, 2]);
        Span<float> weights = stackalloc float[3];
        model.Evaluate(new Vector2(0.3f, -0.5f), weights);
        Assert.Equal(0.7f, weights[0], Tolerance);
        Assert.Equal(0.3f, weights[1], Tolerance);
        Assert.Equal(0f, weights[2], Tolerance);
    }

    [Fact]
    public void Timeline_PlainClip_ClampsFiniteAndWrapsLooping()
    {
        var finite = new ClipTimelineMapping(1.0d, Animation.LoopModeEnum.None, false, 0d, false, 0d, false);
        Assert.Equal(0.4d, finite.SourceTime(0.4d), 9);
        Assert.Equal(1.0d, finite.SourceTime(1.3d), 9);

        var looping = new ClipTimelineMapping(1.0d, Animation.LoopModeEnum.Linear, false, 0d, false, 0d, false);
        Assert.Equal(0.3d, looping.SourceTime(1.3d), 9);
    }

    [Fact]
    public void Timeline_StretchedLoopingCustomTimeline_OffsetsScalesAndWraps()
    {
        // A 1.27 s cycle stretched onto a 1.0667 s timeline and started half a timeline in.
        var mapping = new ClipTimelineMapping(1.2666667d, Animation.LoopModeEnum.Linear, true, 1.0666667d, true, 0.5333333d, false);
        Assert.Equal(0.5333333d * 1.2666667d / 1.0666667d, mapping.SourceTime(0d), 6);
        Assert.Equal((0.8d + 0.5333333d) * 1.2666667d / 1.0666667d % 1.2666667d, mapping.SourceTime(0.8d), 6);
    }

    [Fact]
    public void Timeline_SlicedCustomTimeline_OffsetsWithoutStretch_AndBackwardMirrors()
    {
        var slice = new ClipTimelineMapping(1.63d, Animation.LoopModeEnum.None, true, 0.6d, false, 0.4d, false);
        Assert.Equal(0.4d, slice.SourceTime(0d), 9);
        Assert.Equal(0.9d, slice.SourceTime(0.5d), 9);

        var backward = new ClipTimelineMapping(1.0d, Animation.LoopModeEnum.None, false, 0d, false, 0d, true);
        Assert.Equal(0.75d, backward.SourceTime(0.25d), 9);
    }
}
