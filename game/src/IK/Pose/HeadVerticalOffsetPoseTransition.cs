using Godot;

namespace AlleyCat.IK.Pose;

/// <summary>
/// Pose transition that fires based on vertical head displacement from the calibrated rest
/// viewpoint, with explicit hysteresis thresholds to prevent oscillation around the boundary.
/// </summary>
/// <remarks>
/// <para>
/// The descent metric used is <c>(rest.Y - current.Y)</c>: a positive value means the player's
/// head has dropped below its calibrated rest height. Two transition roles are supported:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       Standing → Crouching: set <see cref="TriggerOnDescent"/> to <c>true</c>. The transition
///       fires when the descent exceeds <see cref="TriggerOffsetMetres"/>.
///     </description>
///   </item>
///   <item>
///     <description>
///       Crouching → Standing: set <see cref="TriggerOnDescent"/> to <c>false</c>. The transition
///       fires when the descent falls below <see cref="ReleaseOffsetMetres"/>.
///     </description>
///   </item>
/// </list>
/// <para>
/// Hysteresis is enforced by keeping <see cref="ReleaseOffsetMetres"/> strictly below
/// <see cref="TriggerOffsetMetres"/>. Configuring both transitions with the same threshold
/// values would reintroduce single-threshold flicker; configure the descent-trigger threshold
/// higher than the ascent-release threshold (see authored <c>.tres</c> files for defaults).
/// </para>
/// </remarks>
[GlobalClass]
public partial class HeadVerticalOffsetPoseTransition : PoseTransition
{
    /// <summary>
    /// Descent in metres above which a Standing → Crouching transition fires.
    /// </summary>
    /// <remarks>
    /// Only evaluated when <see cref="TriggerOnDescent"/> is <c>true</c>.
    /// </remarks>
    [Export]
    public float TriggerOffsetMetres
    {
        get;
        set;
    } = 0.15f;

    /// <summary>
    /// Descent in metres below which a Crouching → Standing transition fires.
    /// </summary>
    /// <remarks>
    /// Only evaluated when <see cref="TriggerOnDescent"/> is <c>false</c>.
    /// </remarks>
    [Export]
    public float ReleaseOffsetMetres
    {
        get;
        set;
    } = 0.08f;

    /// <summary>
    /// When <c>true</c>, the transition fires on head descent (Standing → Crouching). When
    /// <c>false</c>, the transition fires on head ascent (Crouching → Standing) evaluated
    /// against <see cref="ReleaseOffsetMetres"/>.
    /// </summary>
    [Export]
    public bool TriggerOnDescent
    {
        get;
        set;
    } = true;

    /// <inheritdoc />
    public override bool ShouldTransition(PoseStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        float descent = context.ViewpointGlobalRest.Origin.Y - context.CameraTransform.Origin.Y;

        return Evaluate(descent, TriggerOffsetMetres, ReleaseOffsetMetres, TriggerOnDescent);
    }

    /// <summary>
    /// Pure predicate behind <see cref="ShouldTransition"/>.
    /// </summary>
    /// <remarks>
    /// Exposed as a <see langword="public"/> static helper so the hysteresis semantics can be
    /// covered by unit tests without instantiating a Godot <see cref="Resource"/> subclass,
    /// which requires the engine runtime.
    /// </remarks>
    /// <param name="descentMetres">
    /// Head descent in metres, expressed as <c>(rest.Y - current.Y)</c>. Positive means below
    /// the rest viewpoint.
    /// </param>
    /// <param name="triggerOffsetMetres">Descent-trigger threshold.</param>
    /// <param name="releaseOffsetMetres">Ascent-release threshold.</param>
    /// <param name="triggerOnDescent">
    /// When <c>true</c>, fires on descent above <paramref name="triggerOffsetMetres"/>. When
    /// <c>false</c>, fires on ascent below <paramref name="releaseOffsetMetres"/>.
    /// </param>
    /// <returns><c>true</c> when the transition must fire; otherwise <c>false</c>.</returns>
    public static bool Evaluate(
        float descentMetres,
        float triggerOffsetMetres,
        float releaseOffsetMetres,
        bool triggerOnDescent)
        => triggerOnDescent
            ? descentMetres > triggerOffsetMetres
            : descentMetres < releaseOffsetMetres;
}
