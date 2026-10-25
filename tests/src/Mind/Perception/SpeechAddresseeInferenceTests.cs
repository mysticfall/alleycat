using AlleyCat.Mind.Perception;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Mind.Perception;

/// <summary>
/// Unit coverage for the pure facing-cone geometry of speech-addressee inference (AI-006 TR-11): cone membership,
/// multi-addressee resolution, empty-cone undecided, and degenerate-bearings rules.
/// </summary>
public sealed class SpeechAddresseeInferenceTests
{
    private const float ConeAngle = 30f;

    /// <summary>Only characters within the speaker's facing cone are addressees, preserved in candidate order.</summary>
    [Fact]
    public void ResolveFacingConeAddressees_CollectsInConeCandidatesInOrder()
    {
        // The speaker faces world forward (-Z); both staggered candidates ahead of it sit inside a 30-degree cone,
        // while the candidate behind it and the far lateral candidate sit outside.
        IReadOnlyList<string> addressees = SpeechAddresseeInference.ResolveFacingConeAddressees(
            Vector3.Zero,
            Vector3.Forward,
            [
                new SpeechAddresseeCandidate("char:front_left", new Vector3(-1f, 0f, -2f)),
                new SpeechAddresseeCandidate("char:behind", new Vector3(0f, 0f, 2f)),
                new SpeechAddresseeCandidate("char:front_right", new Vector3(1f, 0f, -2f)),
                new SpeechAddresseeCandidate("char:far_side", new Vector3(10f, 0f, -1f)),
            ],
            ConeAngle);

        Assert.Equal(["char:front_left", "char:front_right"], addressees);
    }

    /// <summary>An empty cone result — the speaker facing away from every candidate — is undecided.</summary>
    [Fact]
    public void ResolveFacingConeAddressees_EmptyConeYieldsUndecided()
    {
        IReadOnlyList<string> addressees = SpeechAddresseeInference.ResolveFacingConeAddressees(
            Vector3.Zero,
            Vector3.Forward,
            [new SpeechAddresseeCandidate("char:behind", new Vector3(0f, 0f, 2f))],
            ConeAngle);

        Assert.Empty(addressees);
    }

    /// <summary>Cone membership ignores vertical separation: a raised character keeps its ground bearing.</summary>
    [Fact]
    public void ResolveFacingConeAddressees_IgnoresVerticalSeparation()
    {
        IReadOnlyList<string> addressees = SpeechAddresseeInference.ResolveFacingConeAddressees(
            Vector3.Zero,
            Vector3.Forward,
            [new SpeechAddresseeCandidate("char:raised", new Vector3(0f, 5f, -1f))],
            ConeAngle);

        Assert.Equal(["char:raised"], addressees);
    }

    /// <summary>
    /// A candidate at the speaker's own ground position cannot be outside the cone, matching the project's
    /// zero-separation-as-front bearing rule (AI-006 TR-7).
    /// </summary>
    [Fact]
    public void ResolveFacingConeAddressees_CoLocatedCandidateIsWithinCone()
    {
        IReadOnlyList<string> addressees = SpeechAddresseeInference.ResolveFacingConeAddressees(
            new Vector3(1f, 0f, 1f),
            Vector3.Forward,
            [new SpeechAddresseeCandidate("char:colocated", new Vector3(1f, 2f, 1f))],
            ConeAngle);

        Assert.Equal(["char:colocated"], addressees);
    }

    /// <summary>A facing without a ground-plane component — a vertical gaze — resolves no addressee.</summary>
    [Fact]
    public void ResolveFacingConeAddressees_VerticalFacingYieldsUndecided()
    {
        IReadOnlyList<string> addressees = SpeechAddresseeInference.ResolveFacingConeAddressees(
            Vector3.Zero,
            Vector3.Up,
            [new SpeechAddresseeCandidate("char:anywhere", new Vector3(0f, 0f, -2f))],
            ConeAngle);

        Assert.Empty(addressees);
    }

    /// <summary>A 90-degree cone contains the facing half-plane and excludes the rear half-plane.</summary>
    [Fact]
    public void ResolveFacingConeAddressees_AcceptsWideThresholds()
    {
        IReadOnlyList<string> wide = SpeechAddresseeInference.ResolveFacingConeAddressees(
            Vector3.Zero,
            Vector3.Forward,
            [
                new SpeechAddresseeCandidate("char:beside", new Vector3(2f, 0f, -0.1f)),
                new SpeechAddresseeCandidate("char:rear", new Vector3(0.1f, 0f, 2f)),
            ],
            90f);

        Assert.Equal(["char:beside"], wide);
    }

    /// <summary>The tunable cone angle must be finite and within (0, 180] degrees.</summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(-5f)]
    [InlineData(180.5f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void ValidateConeAngle_RejectsInvalidAngles(float angle)
        => Assert.Throws<ArgumentException>(
            () => SpeechAddresseeInference.ValidateConeAngle(angle));

    /// <summary>Every finite angle within (0, 180] is accepted, including the full half-space maximum.</summary>
    [Theory]
    [InlineData(0.5f)]
    [InlineData(30f)]
    [InlineData(180f)]
    public void ValidateConeAngle_AcceptsValidAngles(float angle)
        => SpeechAddresseeInference.ValidateConeAngle(angle);
}
