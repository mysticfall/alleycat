using AlleyCat.Common;
using Godot;

namespace AlleyCat.IK;

// Player-only, reversible diagnostic override. Never installs on an NPC or changes shared resources.
internal sealed class PlayerAnimationBodyDiagnostic : IDisposable
{
    private readonly SkeletonModifier3D[] _pipeline;
    private readonly (SkeletonModifier3D Modifier, bool Active, float Influence)[] _saved;

    public PlayerAnimationBodyDiagnostic(Skeleton3D skeleton)
    {
        string[] names = ["HipReconciliationModifier", "NeckTwistDisperser", "RightArmIKController", "LeftArmIKController",
            "RightArmTwoBoneIKController", "LeftArmTwoBoneIKController", "RightHandCopyRotation", "LeftHandCopyRotation",
            "ForearmTwistModifier", "OpticalFingerTrackingModifier", "RightLegIKController", "LeftLegIKController",
            "RightLegTwoBoneIKController", "LeftLegTwoBoneIKController", "CopyRightFootRotation", "CopyLeftFootRotation"];
        _saved = [.. names.Select(name => skeleton.RequireNode<SkeletonModifier3D>(name))
            .Select(modifier => (modifier, modifier.Active, modifier.Influence))];
        _pipeline = [.. skeleton.GetChildren().OfType<SkeletonModifier3D>()];
        // Provider/authority stages refresh influences. Reassert before their downstream consumers,
        // not merely once when the mode is enabled. No bone values or feedback are substituted.
        foreach (SkeletonModifier3D modifier in _pipeline)
        {
            modifier.ModificationProcessed += Enforce;
        }
        Enforce();
    }

    private void Enforce()
    {
        foreach ((SkeletonModifier3D modifier, _, _) in _saved)
        {
            modifier.Active = false;
            modifier.Influence = 0;
        }
    }

    public void Dispose()
    {
        foreach (SkeletonModifier3D modifier in _pipeline)
        {
            if (GodotObject.IsInstanceValid(modifier))
            {
                modifier.ModificationProcessed -= Enforce;
            }
        }
        foreach ((SkeletonModifier3D modifier, bool active, float influence) in _saved)
        {
            if (!GodotObject.IsInstanceValid(modifier))
            {
                continue;
            }
            modifier.Active = active;
            modifier.Influence = influence;
        }
    }
}
