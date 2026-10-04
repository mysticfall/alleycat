using AlleyCat.IK;
using Xunit;

#pragma warning disable CS1591

namespace AlleyCat.Tests.IK;

/// <summary>
/// IK-003 Technical Requirement 8 resolution span: scoped to the resolving skeleton and thread, nestable, and harmless
/// when a scope or a copy of it is disposed twice.
/// </summary>
public sealed class FootTargetResolutionStageTests
{
    [Fact]
    public void Enter_ActivatesOnlyTheOwnersSpan()
    {
        object first = new();
        object second = new();
        Assert.False(FootTargetResolutionStage.IsActiveFor(first));

        using (FootTargetResolutionStage.Enter(first))
        {
            Assert.True(FootTargetResolutionStage.IsActiveFor(first));
            Assert.False(FootTargetResolutionStage.IsActiveFor(second));
            Assert.False(FootTargetResolutionStage.IsActiveFor(null));
        }

        Assert.False(FootTargetResolutionStage.IsActiveFor(first));
    }

    [Fact]
    public void NestedSpans_RestoreTheOuterOwner()
    {
        object outer = new();
        object inner = new();
        using FootTargetResolutionStage.Scope outerScope = FootTargetResolutionStage.Enter(outer);
        using (FootTargetResolutionStage.Enter(inner))
        {
            Assert.True(FootTargetResolutionStage.IsActiveFor(inner));
            Assert.False(FootTargetResolutionStage.IsActiveFor(outer));
        }

        Assert.True(FootTargetResolutionStage.IsActiveFor(outer));
    }

    [Fact]
    public void DoubleDisposeOfACopiedScope_IsHarmless()
    {
        object outer = new();
        object inner = new();
        FootTargetResolutionStage.Scope outerScope = FootTargetResolutionStage.Enter(outer);
        FootTargetResolutionStage.Scope innerScope = FootTargetResolutionStage.Enter(inner);
        FootTargetResolutionStage.Scope copy = innerScope;

        innerScope.Dispose();
        copy.Dispose();
        innerScope.Dispose();
        Assert.True(FootTargetResolutionStage.IsActiveFor(outer), "A repeated dispose of the inner scope closed the outer span.");

        // A later span is not closed by a stale copy of an earlier scope either.
        FootTargetResolutionStage.Scope later = FootTargetResolutionStage.Enter(inner);
        copy.Dispose();
        Assert.True(FootTargetResolutionStage.IsActiveFor(inner), "A stale scope copy closed a later span.");
        later.Dispose();

        outerScope.Dispose();
        outerScope.Dispose();
        default(FootTargetResolutionStage.Scope).Dispose();
        Assert.False(FootTargetResolutionStage.IsActiveFor(outer));
        Assert.False(FootTargetResolutionStage.IsActiveFor(inner));
    }

    [Fact]
    public void SpanOnOneThread_IsNotActiveOnAnother()
    {
        object owner = new();
        using FootTargetResolutionStage.Scope scope = FootTargetResolutionStage.Enter(owner);
        bool activeElsewhere = true;
        var thread = new Thread(() => activeElsewhere = FootTargetResolutionStage.IsActiveFor(owner));
        thread.Start();
        thread.Join();

        Assert.True(FootTargetResolutionStage.IsActiveFor(owner));
        Assert.False(activeElsewhere);
    }
}
