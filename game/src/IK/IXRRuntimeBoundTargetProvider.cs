using AlleyCat.XR;

namespace AlleyCat.IK;

/// <summary>
/// IK target provider that can bind itself to the active XR runtime without caller-specific type knowledge.
/// </summary>
public interface IXRRuntimeBoundTargetProvider
{
    /// <summary>
    /// Attempts to resolve this provider's XR source from the active runtime.
    /// </summary>
    bool TryBind(IXRRuntime runtime);
}
