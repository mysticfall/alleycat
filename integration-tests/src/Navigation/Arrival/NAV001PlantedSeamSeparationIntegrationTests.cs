using System.Globalization;
using System.Reflection;
using AlleyCat.Control.Locomotion;
using AlleyCat.Navigation.Arrival;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Navigation.Arrival.NAV001ArrivalPlannerIntegrationTests;

namespace AlleyCat.IntegrationTests.Navigation.Arrival;

/// <summary>Read-only split of the production seam metric on both production trajectory tables.</summary>
public sealed class NAV001PlantedSeamSeparationIntegrationTests
{
    private const string Evidence = "res://temp/nav001-planted-seam-separation";
    // Reuse precisely the production sampling grid without exposing a new production API.
    private static readonly Action<SeamClip, double, double, List<double>> _addKeys = typeof(SeamClip)
        .GetMethod("AddKeyOffsets", BindingFlags.Instance | BindingFlags.NonPublic)!
        .CreateDelegate<Action<SeamClip, double, double, List<double>>>();

    /// <summary>Measure all phase-agreeing candidates for the female rig.</summary>
    [Fact]
    public Task Female() => MeasureAsync(NPCNavigationRig.Female);

    /// <summary>Measure all phase-agreeing candidates for the male rig.</summary>
    [Fact]
    public Task Male() => MeasureAsync(NPCNavigationRig.Male);

    private static async Task MeasureAsync(NPCNavigationRig rig)
    {
        await using NPCNavigationRigFixture fixture = await NPCNavigationRigFixture.CreateAsync(rig);
        await fixture.StepAsync(NAV001ArrivalOutcomeIntegrationTests.WarmUpPhysicsTicks);
        Binding binding = Bind(fixture);
        string directory = EvidenceDestinations.Resolve(ProjectSettings.GlobalizePath(Evidence));
        using var candidates = new StreamWriter(Path.Combine(directory, $"{rig}-candidates.csv"));
        using var pairs = new StreamWriter(Path.Combine(directory, $"{rig}-pairs.csv"));
        candidates.WriteLine("from,to,a,b,left_planted_m,left_swing_m,right_planted_m,right_swing_m,existing_max_m,usable,phase_boundary_disagreements");
        pairs.WriteLine("from,to,kind,crossfade_s,window_s,candidates,usable,best_planted_m,best_swing_m,best_a,best_b,count10,count20,count30,count50,usable10,usable20,usable30,usable50,current190,min_planted_with_swing190_m,planner_uses");
        long checkedCount = 0;
        foreach (SeamPair pair in binding.Seams.Pairs)
        {
            List<(SeamCandidate Candidate, Split Value)> values = [];
            foreach (SeamCandidate candidate in pair.Candidates)
            {
                Split split = Measure(pair, candidate);
                Assert.Equal(candidate.LeftSeparation, Math.Max(split.LeftPlanted, split.LeftSwing));
                Assert.Equal(candidate.RightSeparation, Math.Max(split.RightPlanted, split.RightSwing));
                Assert.Equal(candidate.MaxSeparation, Math.Max(split.Planted, split.Swing));
                values.Add((candidate, split));
                checkedCount++;
                candidates.WriteLine(FormattableString.Invariant($"{pair.Outgoing.Role},{pair.Incoming.Role},{candidate.OutgoingTime:R},{candidate.IncomingTime:R},{split.LeftPlanted:R},{split.LeftSwing:R},{split.RightPlanted:R},{split.RightSwing:R},{candidate.MaxSeparation:R},{pair.IsUsable(candidate)},{split.BoundaryDisagreements}"));
            }

            (SeamCandidate Candidate, Split Value)[] ordered = [.. values.OrderBy(value => value.Value.Planted).ThenBy(value => value.Value.Swing)];
            string best = ordered.Length == 0 ? ",,," : FormattableString.Invariant($"{ordered[0].Value.Planted:R},{ordered[0].Value.Swing:R},{ordered[0].Candidate.OutgoingTime:R},{ordered[0].Candidate.IncomingTime:R}");
            int[] counts = [.. new[] { 0.01f, 0.02f, 0.03f, 0.05f }.Select(tolerance => values.Count(value => value.Value.Planted <= tolerance && value.Value.Swing <= 0.19f))];
            int[] usable = [.. new[] { 0.01f, 0.02f, 0.03f, 0.05f }.Select(tolerance => values.Count(value => pair.IsUsable(value.Candidate) && value.Value.Planted <= tolerance && value.Value.Swing <= 0.19f))];
            float minimum = values.Where(value => pair.IsUsable(value.Candidate) && value.Value.Swing <= 0.19f).Select(value => value.Value.Planted).DefaultIfEmpty(float.PositiveInfinity).Min();
            pairs.WriteLine(FormattableString.Invariant($"{pair.Outgoing.Role},{pair.Incoming.Role},{pair.Kind},{pair.CrossfadeSeconds:R},{pair.WindowSeconds:R},{values.Count},{values.Count(value => pair.IsUsable(value.Candidate))},{best},{string.Join(',', counts)},{string.Join(',', usable)},{values.Count(value => pair.IsUsable(value.Candidate) && value.Candidate.MaxSeparation <= 0.19f)},{minimum:R},{binding.Planner.UsablePairs.Contains(pair)}"));
        }

        File.WriteAllText(Path.Combine(directory, $"{rig}-assertions.txt"), $"Exact per-foot and combined float equality passed for {checkedCount.ToString(CultureInfo.InvariantCulture)} phase-agreeing candidates.\n");
        await TeleportToIdleAtOriginAsync(fixture, binding);
        ArrivalPlan plan = binding.Planner.Plan(IdleStart(fixture, binding), new ArrivalTarget(new Vector2(0f, -4f), 0f));
        Assert.Equal(ArrivalPlanStatus.Planned, plan.Status);
        using var selected = new StreamWriter(Path.Combine(directory, $"{rig}-long-straight-selected.csv"));
        selected.WriteLine("from,to,a,b,left_planted_m,left_swing_m,right_planted_m,right_swing_m,existing_max_m");
        for (int index = 1; index < plan.Segments.Length; index++)
        {
            ArrivalPlanSegment outgoing = plan.Segments[index - 1];
            ArrivalPlanSegment incoming = plan.Segments[index];
            Assert.True(binding.Seams.TryGetPair(outgoing.Role, incoming.Role, out SeamPair? pair));
            Assert.True(binding.Seams.TryEvaluateSeam(outgoing.Role, incoming.Role, outgoing.ExitSeconds, incoming.EntrySeconds, out SeamCandidate candidate));
            Split split = Measure(pair!, candidate);
            selected.WriteLine(FormattableString.Invariant($"{outgoing.Role},{incoming.Role},{candidate.OutgoingTime:R},{candidate.IncomingTime:R},{split.LeftPlanted:R},{split.LeftSwing:R},{split.RightPlanted:R},{split.RightSwing:R},{candidate.MaxSeparation:R}"));
        }
    }

    private static Split Measure(SeamPair pair, SeamCandidate candidate)
    {
        double a = pair.Outgoing.WrapsPlayback ? pair.Outgoing.Normalise(candidate.OutgoingTime + pair.EntryLagSeconds) : candidate.OutgoingTime + pair.EntryLagSeconds;
        double b = candidate.IncomingTime;
        List<double> offsets = [0d];
        _addKeys(pair.Outgoing, a, pair.WindowSeconds, offsets);
        _addKeys(pair.Incoming, b, pair.WindowSeconds, offsets);
        offsets.Add(pair.WindowSeconds);
        float[] maxima = new float[4];
        int disagreements = 0;
        foreach (double tau in offsets)
        {
            foreach (StandingLocomotionAnatomicalSide foot in new[] { StandingLocomotionAnatomicalSide.Left, StandingLocomotionAnatomicalSide.Right })
            {
                float separation = Math.Max(
                    pair.Outgoing.Ball(foot, a + tau, false).DistanceTo(pair.Incoming.Ball(foot, b + tau, false)),
                    pair.Outgoing.Ball(foot, a + tau, true).DistanceTo(pair.Incoming.Ball(foot, b + tau, true)));
                // Phase agreement is defined on [0,w), whereas the metric includes w. Classify
                // boundary samples by the common state just inside the measured window.
                double phaseTau = tau >= pair.WindowSeconds ? Math.Max(0d, tau - 1e-8d) : tau + 1e-8d;
                bool planted = pair.Outgoing.IsPlanted(foot, a + phaseTau);
                if (planted != pair.Incoming.IsPlanted(foot, b + phaseTau))
                {
                    disagreements++;
                }

                int index = (foot == StandingLocomotionAnatomicalSide.Left ? 0 : 2) + (planted ? 0 : 1);
                maxima[index] = Math.Max(maxima[index], separation);
            }
        }

        return new Split(maxima[0], maxima[1], maxima[2], maxima[3], disagreements);
    }

    private readonly record struct Split(float LeftPlanted, float LeftSwing, float RightPlanted, float RightSwing, int BoundaryDisagreements)
    {
        public float Planted => Math.Max(LeftPlanted, RightPlanted);
        public float Swing => Math.Max(LeftSwing, RightSwing);
    }
}
