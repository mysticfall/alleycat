using Godot;

namespace AlleyCat.IK.Pose;

/// <summary>
/// Pose transition that fires based on normalised local head displacement from calibrated rest.
/// </summary>
/// <remarks>
/// <para>
/// The offset metric used is <see cref="PoseStateContext.NormalizedHeadLocalOffset"/>. This
/// vector is expressed in skeleton-local space and normalised by calibrated local head height, so
/// thresholds are authored in fractions rather than metres.
/// </para>
/// <para>
/// Direction semantics are explicit and deterministic:
/// </para>
/// <list type="bullet">
/// <item><description>Upward uses positive local Y from rest.</description></item>
/// <item><description>Downward uses negative local Y from rest.</description></item>
/// <item><description>Forward uses negative local Z from rest (Godot forward).</description></item>
/// <item><description>Backward uses positive local Z from rest.</description></item>
/// </list>
/// <para>
/// <see cref="Threshold"/> keeps positive-threshold authoring semantics. The selected direction
/// determines which signed axis and comparison are applied.
/// </para>
/// </remarks>
[GlobalClass]
public partial class HeadOffsetPoseTransition : PoseTransition
{
    /// <summary>
    /// Local-offset direction used to evaluate the transition threshold.
    /// </summary>
    public enum TransitionDirection
    {
        /// <summary>
        /// Fires when head movement along local Z is forward (-Z) past threshold.
        /// </summary>
        Forward,

        /// <summary>
        /// Fires when head movement along local Z is backward (+Z) past threshold.
        /// </summary>
        Backward,

        /// <summary>
        /// Fires when head movement along local Y is upward (+Y) past threshold.
        /// </summary>
        Upward,

        /// <summary>
        /// Fires when head movement along local Y is downward (-Y) past threshold.
        /// </summary>
        Downward,
    }

    /// <summary>
    /// Positive normalised displacement threshold used by <see cref="ShouldTransition"/>.
    /// </summary>
    [Export]
    public float Threshold
    {
        get;
        set;
    } = 0.02f;

    /// <summary>
    /// Axis direction evaluated against <see cref="Threshold"/>.
    /// </summary>
    [Export]
    public TransitionDirection Direction
    {
        get;
        set;
    } = TransitionDirection.Downward;

    /// <inheritdoc />
    public override bool ShouldTransition(PoseStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Evaluate(context.NormalizedHeadLocalOffset, Threshold, Direction);
    }

    /// <summary>
    /// Pure predicate behind <see cref="ShouldTransition"/>.
    /// </summary>
    /// <remarks>
    /// Exposed as a <see langword="public"/> static helper so the directional threshold semantics
    /// covered by unit tests without instantiating a Godot <see cref="Resource"/> subclass,
    /// which requires the engine runtime.
    /// </remarks>
    /// <param name="normalizedHeadLocalOffset">
    /// Head local offset from rest, normalised by rest local head height.
    /// </param>
    /// <param name="threshold">Direction-dependent trigger threshold.</param>
    /// <param name="direction">Direction to evaluate against <paramref name="threshold"/>.</param>
    /// <returns><c>true</c> when the transition must fire; otherwise <c>false</c>.</returns>
    public static bool Evaluate(
        Vector3 normalizedHeadLocalOffset,
        float threshold,
        TransitionDirection direction)
    {
        float positiveThreshold = MathF.Abs(threshold);

        return direction switch
        {
            TransitionDirection.Forward => normalizedHeadLocalOffset.Z < -positiveThreshold,
            TransitionDirection.Backward => normalizedHeadLocalOffset.Z > positiveThreshold,
            TransitionDirection.Upward => normalizedHeadLocalOffset.Y > positiveThreshold,
            TransitionDirection.Downward => normalizedHeadLocalOffset.Y < -positiveThreshold,
            _ => false,
        };
    }
}
