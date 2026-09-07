using AlleyCat.Rigging;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Rigging;

/// <summary>Unit coverage for authority changes that must reset a side's twist branch.</summary>
public sealed class ForearmTwistAuthorityTemporalStateTests
{
    /// <summary>Normal pose movement retains one authority epoch and temporal branch.</summary>
    [Fact]
    public void Observe_MovementWithinOneAuthority_DoesNotStartANewBranch()
    {
        ForearmTwistAuthorityTemporalState state = new();

        Assert.True(state.Observe(Ready(ForearmTwistAuthorityKind.IKProvider, 11, 4)));
        Assert.False(state.Observe(Ready(ForearmTwistAuthorityKind.IKProvider, 11, 4)));
    }

    /// <summary>Ownership metadata changes independently start a fresh temporal branch.</summary>
    [Fact]
    public void Observe_AuthorityIdentityKindOrEpochChange_StartsAFreshBranch()
    {
        ForearmTwistAuthorityTemporalState state = new();

        Assert.True(state.Observe(Ready(ForearmTwistAuthorityKind.IKProvider, 11, 4)));
        Assert.True(state.Observe(Ready(ForearmTwistAuthorityKind.FrozenTracking, 11, 5)));
        Assert.True(state.Observe(Ready(ForearmTwistAuthorityKind.IKProvider, 11, 6)));
        Assert.True(state.Observe(Ready(ForearmTwistAuthorityKind.IKProvider, 12, 6)));
    }

    /// <summary>An unavailable pose discards temporal state until the next usable pose arrives.</summary>
    [Fact]
    public void Observe_UnreadySample_ResetsUntilAReadySampleStartsFresh()
    {
        ForearmTwistAuthorityTemporalState state = new();

        Assert.True(state.Observe(Ready(ForearmTwistAuthorityKind.Animation, 1, 2)));
        Assert.False(state.Observe(ForearmTwistAuthoritySample.Unready(3)));
        Assert.True(state.Observe(Ready(ForearmTwistAuthorityKind.Animation, 1, 2)));
    }

    private static ForearmTwistAuthoritySample Ready(ForearmTwistAuthorityKind kind, ulong sourceIdentity, ulong epoch)
        => new(Transform3D.Identity, 0.0f, true, kind, sourceIdentity, epoch, 1);
}
