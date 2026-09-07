using Godot;

namespace AlleyCat.IK;

/// <summary>
/// Resolves the first applicable finite target intent from providers in authored array order.
/// </summary>
[GlobalClass]
public partial class PriorityIKTargetIntentProvider : IKTargetIntentProvider
{
    /// <summary>
    /// Providers queried in deterministic first-to-last order.
    /// </summary>
    [Export]
    public IKTargetIntentProvider[] Providers { get; set; } = [];

    /// <inheritdoc />
    public override IKTargetIntent GetTargetIntent()
        => TryGetTargetIntent(out IKTargetIntentResolution resolution)
            ? resolution.Intent
            : new IKTargetIntent(Transform3D.Identity, 0.0f);

    /// <inheritdoc />
    public override bool TryGetTargetIntent(out IKTargetIntentResolution resolution)
    {
        IKTargetIntentProvider[] providers = Providers;
        for (int providerIndex = 0; providerIndex < providers.Length; providerIndex++)
        {
            IKTargetIntentProvider? provider = providers[providerIndex];
            if (provider is null || ReferenceEquals(provider, this) || !IsInstanceValid(provider))
            {
                continue;
            }

            if (provider.TryGetTargetIntent(out IKTargetIntentResolution candidate) && candidate.IsFinite)
            {
                resolution = candidate;
                return true;
            }
        }

        resolution = default;
        return false;
    }
}
