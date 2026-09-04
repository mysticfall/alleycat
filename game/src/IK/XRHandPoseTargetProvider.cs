using AlleyCat.Rigging;
using AlleyCat.XR;
using AlleyCat.XR.HandTracking;
using Godot;

namespace AlleyCat.IK;

/// <summary>
/// IK target provider that follows the committed per-side XR hand-pose source wrist (XR-002 TR7, TR13).
/// </summary>
/// <remarks>
/// Replaces the controller-only provider at the VRIK fallback-source seam: in controller mode the live calibrated
/// controller hand-position anchor drives the intent (XR-002 TR8); while the committed mode is optical the
/// calibrated world-space optical wrist drives it, retaining its frozen last valid world transform while tracking
/// is lost (XR-002 TR5, TR25). The intent never degrades to identity or zero influence merely because optical
/// tracking was temporarily lost after a valid pose exists; zero influence is only returned before any valid pose
/// exists and no safe controller default is available.
/// </remarks>
[GlobalClass]
public partial class XRHandPoseTargetProvider : IKTargetIntentProvider
{
    /// <summary>
    /// Limb side used to select the corresponding hand-pose source during runtime binding.
    /// </summary>
    [Export]
    public LimbSide Side
    {
        get;
        set;
    } = LimbSide.Right;

    /// <summary>
    /// Resolved runtime hand-pose source, or <see langword="null" /> before binding.
    /// </summary>
    public IXRHandPoseSource? ResolvedSource
    {
        get;
        private set;
    }

    /// <summary>
    /// Desired influence while the XR hand-pose source is available.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float DesiredInfluence { get; set; } = 1.0f;

    private bool TryResolveSource()
    {
        IXRRuntime? runtime = ResolveXRRuntime();

        if (runtime is null || !IsRuntimeValid(runtime))
        {
            ResolvedSource = null;
            return false;
        }

        try
        {
            ResolvedSource = runtime.GetHandPoseSource(Side);
        }
        catch (InvalidOperationException)
        {
            ResolvedSource = null;
            return false;
        }

        return true;
    }

    /// <inheritdoc />
    public override IKTargetIntent GetTargetIntent()
    {
        _ = TryResolveSource();

        return ResolvedSource is not null && ResolvedSource.TryGetCalibratedWristTransform(out Transform3D wrist)
            ? new IKTargetIntent(wrist, DesiredInfluence)
            : new IKTargetIntent(Transform3D.Identity, 0.0f);
    }

    private static bool IsRuntimeValid(IXRRuntime runtime)
        => runtime is not GodotObject godotObject || IsInstanceValid(godotObject);

    private static IXRRuntime? ResolveXRRuntime()
    {
        try
        {
            XRManager? xrManager = Game.Instance.GetService<XRManager>();
            return xrManager?.Runtime;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
