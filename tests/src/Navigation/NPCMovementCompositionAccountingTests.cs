using AlleyCat.IK;
using AlleyCat.Navigation;
using Godot;
using Xunit;

#pragma warning disable CS1591

namespace AlleyCat.Tests.Navigation;

public sealed class NPCMovementCompositionAccountingTests
{
    [Fact]
    public void Totals_ReconcileIdenticalTickWindowWithoutAddingDesiredInput()
    {
        NPCMovementCompositionFrame first = Frame(
            authored: new Vector3(0f, 0f, -0.1f),
            desiredInput: new Vector3(0f, 0f, -0.2f),
            synthetic: new Vector3(0.01f, 0f, 0f),
            contact: new Vector3(0f, 0f, -0.02f));
        NPCMovementCompositionFrame second = Frame(
            authored: new Vector3(0f, 0f, -0.15f),
            desiredInput: new Vector3(0f, 0f, -0.2f),
            synthetic: new Vector3(0.02f, 0f, 0f),
            contact: Vector3.Zero);

        NPCMovementCompositionTotals totals = default;
        totals = totals.Add(first).Add(second);

        Assert.Equal(2, totals.TickCount);
        Assert.Equal(new Vector3(0f, 0f, -0.25f), totals.AuthoredRoot);
        Assert.Equal(new Vector3(0f, 0f, -0.4f), totals.ProgrammeDesiredInput);
        Assert.Equal(Vector3.Zero, totals.ProgrammeDesiredContribution);
        Assert.Equal(totals.AuthoredRoot + totals.Synthetic + totals.Contact, totals.Requested);
        Assert.True(totals.CompositionError.IsEqualApprox(Vector3.Zero));
        Assert.True(totals.RealisationError.IsEqualApprox(Vector3.Zero));
    }

    [Fact]
    public void Totals_ExposeCollisionRemainderAsRealisationError()
    {
        Vector3 requested = new(0f, 0f, -0.1f);
        Vector3 remainder = new(0f, 0f, -0.03f);
        NPCMovementCompositionFrame frame = Frame(
            authored: requested,
            desiredInput: Vector3.Zero,
            synthetic: Vector3.Zero,
            contact: Vector3.Zero) with
        {
            CollisionTravel = requested - remainder,
            CollisionRemainder = remainder,
            RealisedWorldDelta = requested - remainder,
            Collided = true,
        };

        NPCMovementCompositionTotals totals = default;
        totals = totals.Add(frame);

        Assert.True(totals.RealisationError.IsEqualApprox(-remainder));
        Assert.Equal(remainder, totals.CollisionRemainder);
    }

    [Fact]
    public void AuthorisedContactContribution_IsComposedExactlyOnce()
    {
        var contribution = new ContactLockMotionContribution(
            new(new Vector3(0.01f, 0f, -0.02f), 0.1f),
            new(new Vector3(0.01f, 0f, -0.02f), 0.1f),
            ContactLockFeasibilityReason.Valid,
            true);
        NPCMovementCompositionFrame frame = Frame(
            authored: new Vector3(0f, 0f, -0.1f),
            desiredInput: Vector3.Zero,
            synthetic: new Vector3(0.02f, 0f, 0f),
            contact: contribution.Authorised.Translation);

        Assert.True(contribution.IsAuthorised);
        Assert.Equal(contribution.Authorised.Translation, frame.ContactWorldDelta);
        Assert.Equal(
            frame.AuthoredRootWorldDelta + frame.ProgrammeDesiredContribution + frame.SyntheticWorldDelta
                + contribution.Authorised.Translation,
            frame.RequestedWorldDelta);
        Assert.True(frame.CompositionError.IsEqualApprox(Vector3.Zero));
    }

    private static NPCMovementCompositionFrame Frame(
        Vector3 authored,
        Vector3 desiredInput,
        Vector3 synthetic,
        Vector3 contact)
    {
        Vector3 requested = authored + synthetic + contact;
        return new(
            1,
            authored,
            authored,
            authored,
            desiredInput,
            Vector3.Zero,
            synthetic,
            contact,
            requested * 60f,
            requested,
            requested,
            Vector3.Zero,
            requested,
            Collided: false);
    }
}
