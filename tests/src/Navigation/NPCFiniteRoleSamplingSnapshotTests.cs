using System.Collections.Immutable;
using AlleyCat.Navigation;
using Xunit;

#pragma warning disable CS1591

namespace AlleyCat.Tests.Navigation;

public sealed class NPCFiniteRoleSamplingSnapshotTests
{
    [Fact]
    public void Require_SelectsCurrentSnapshotValueAndDoesNotFallbackToPreviousSnapshot()
    {
        var original = new NPCFiniteRoleSamplingSnapshot<int>(
            ImmutableDictionary<string, int>.Empty.Add("f2i_l", 11));
        var replacement = new NPCFiniteRoleSamplingSnapshot<int>(
            ImmutableDictionary<string, int>.Empty.Add("f2i_l", 29));

        Assert.Equal(11, original.Require("f2i_l"));
        Assert.Equal(29, replacement.Require("f2i_l"));
        _ = Assert.Throws<InvalidOperationException>(() => replacement.Require("f2i_r"));
    }
}
