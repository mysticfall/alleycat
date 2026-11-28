using System.Text.Json;
using AlleyCat.Navigation;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

public sealed partial class PlayerMotionBricksIntegrationTests
{
    /// <summary>Two source cycles service four actual endpoints before the one final idle connection.</summary>
    [Headless]
    [Fact]
    public Task FiniteNAVTurn_ServicesAllCuesBeforeFiniteArrival() => RunFixedFiniteTurnAsync(false);

    /// <summary>Cancelling with a successor pending reports acquired actor yaw rather than contrary generated hips.</summary>
    [Headless]
    [Fact]
    public Task FiniteNAVTurn_CancelPendingSuccessorReportsHeldActor() => RunFixedFiniteTurnAsync(true);

    private static async Task RunFixedFiniteTurnAsync(bool cancel)
    {
        Vector3 start = new(.5f, 0, -1.5f);
        await using NavigationFixedStepFixture fixture = await NavigationFixedStepFixture.CreateAsync(start, 0);
        MotionBricksNavigation nav = fixture.Navigation;
        const float requestedYaw = 2.8f; // Between one 120° source allocation and the 180° shortest-turn limit.
        var submitted = new List<long>();
        var endpoints = new List<long>();
        var played = new HashSet<long>();
        var targets = new List<Transform3D[][]>();
        float priorYaw = 0;
        fixture.Provider.Frames = 30;
        fixture.Provider.Response = (context, target, count) =>
        {
            var pose = (Transform3D[])target[0].Clone();
            pose[0].Origin = context[3][0].Origin;
            pose[0].Basis = new Basis(Vector3.Up, -.8f); // Deliberately disagrees with game-owned actor yaw.
            return [.. Enumerable.Repeat(pose, count)];
        };
        nav.WindowSubmitted += (ticket, _, target) =>
        {
            submitted.Add(ticket);
            targets.Add(target);
        };
        double? firstCuePlaybackFraction = null;
        nav.PlaybackObserved = (ticket, frames, frame) =>
        {
            if (frames is null)
            {
                return;
            }
            _ = played.Add(ticket);
            firstCuePlaybackFraction = ticket == submitted[0] ? frame / (frames.Length - 1) : null;
            if (frame >= frames.Length - 1)
            {
                endpoints.Add(ticket);
            }
        };
        double initialHeading = nav.RecordingAnatomicalHeading!.Value - Math.PI;
        Assert.Equal(NavigationDestinationResult.Accepted, nav.SetDestination(new(new Basis(Vector3.Up, requestedYaw), start), []));
        int released = 0;
        int firstCueSteps = 0;
        for (int tick = 0; tick <= 866; tick++)
        {
            await fixture.StepAsync();
            if (firstCuePlaybackFraction is { } fraction)
            {
                // Independently measured 8.026955° at the first landmark of the 120° allocation.
                double expected = initialHeading + (fraction * 8.026955 * Math.PI / 180);
                Assert.InRange(Math.Abs(Math.IEEERemainder(fixture.Actor.GlobalRotation.Y - expected, Math.Tau)), 0, .00001);
                firstCueSteps++;
            }
            if (released == submitted.Count)
            {
                continue;
            }
            long ticket = submitted[released];
            if (cancel && released == 1)
            {
                Assert.Equal(submitted[0], Assert.Single(endpoints));
                await fixture.Provider.WaitForStartAsync(); // Pending successor.
                await fixture.StepAsync(); // Observe held actor while second cue is in flight.
                float actual = fixture.Actor.GlobalRotation.Y;
                Assert.True(actual > Mathf.DegToRad(nav.FacingToleranceDegrees));
                Assert.InRange(Math.Abs(Math.IEEERemainder(nav.RecordingMeasuredTransform.Basis.GetEuler().Y - actual, Math.Tau)), 0, .00001);
                nav.ClearDestination();
                await fixture.ReleaseAsync(ticket, expectDiscard: true, alreadyStarted: true);
                await fixture.StepAsync();
                NavigationTerminalOutcomeRecord outcome = Assert.Single(nav.TerminalOutcomes);
                Assert.Equal(NavigationTerminalReason.Cancellation, outcome.Reason);
                Assert.InRange(Math.Abs(Math.IEEERemainder(outcome.FinalTransform.YawRadians - actual, Math.Tau)), 0, .00001);
                Assert.DoesNotContain(ticket, played);
                return;
            }
            Assert.Equal(released, endpoints.Count);
            if (released > 0)
            {
                float actorYaw = fixture.Actor.GlobalRotation.Y;
                Assert.True(actorYaw > priorYaw);
                priorYaw = actorYaw;
            }
            await fixture.ReleaseAsync(ticket);
            released++;
        }
        Assert.False(cancel, "A played first cue must submit its pending successor.");
        Assert.True(firstCueSteps >= 50);
        Assert.Equal(5, submitted.Count);
        Assert.Equal(submitted, endpoints);
        using var source = JsonDocument.Parse(File.ReadAllText(ProjectSettings.GlobalizePath(
            "res://assets/animations/locomotion/feminine/landing-ablation-01.json")));
        JsonElement rows = source.RootElement.GetProperty("samples");
        // The unchanged left source has two authored primary landmarks, repeated for the second allocation.
        int[] authoredCues = [947, 961, 947, 961];
        double sourceStart = rows[945].GetProperty("heading_radians").GetDouble(); // Source frame 946.
        double sourceEnd = rows[1005].GetProperty("heading_radians").GetDouble(); // Exclusive boundary frame 1006.
        double sourceSpan = Math.IEEERemainder(sourceEnd - sourceStart, Math.Tau);
        const double fullCycle = 2 * Math.PI / 3; // ANIM-002: 120° maximum per cycle.
        double requestedArc = Math.IEEERemainder(requestedYaw - initialHeading, Math.Tau);
        Assert.True(requestedArc is > fullCycle and < Math.PI);
        for (int cue = 0; cue < authoredCues.Length; cue++)
        {
            double anchor = cue < 2 ? initialHeading : initialHeading + fullCycle;
            double allocation = cue < 2 ? fullCycle : requestedArc - fullCycle;
            Assert.Equal(4, targets[cue].Length);
            for (int row = 0; row < 4; row++)
            {
                JsonElement sample = rows[authoredCues[cue] + row - 1];
                double progress = Math.IEEERemainder(sample.GetProperty("heading_radians").GetDouble() - sourceStart, Math.Tau);
                double expectedYaw = anchor + (allocation * progress / sourceSpan);
                Vector3 forward = targets[cue][row][0].Basis.Z;
                double actualYaw = Math.Atan2(forward.X, forward.Z);
                double yawError = Math.Abs(Math.IEEERemainder(actualYaw - expectedYaw, Math.Tau));
                Assert.True(yawError <= .00001, $"Cue {cue + 1}, row {row + 1}: root yaw error {yawError:R} rad.");
                JsonElement authored = sample.GetProperty("pose");
                for (int joint = 1; joint < targets[cue][row].Length; joint++)
                {
                    float[] values = [.. authored[joint].EnumerateArray().Select(v => v.GetSingle())];
                    Transform3D local = new(new Basis(new Quaternion(values[3], values[4], values[5], values[6])),
                        new Vector3(values[0], values[1], values[2]));
                    Assert.True(local.IsEqualApprox(targets[cue][row][joint]));
                }
            }
        }
        NavigationTerminalOutcomeRecord completed = Assert.Single(nav.TerminalOutcomes);
        Assert.Equal(NavigationTerminalReason.ArrivalSequenceCompleted, completed.Reason);
        Assert.InRange(Math.Abs(Math.IEEERemainder(completed.FinalTransform.YawRadians - requestedYaw, Math.Tau)), 0, .00001);
        Assert.InRange(Math.Abs(Math.IEEERemainder(fixture.Actor.GlobalRotation.Y - requestedYaw, Math.Tau)), 0, .00001);
        Assert.Equal("settled-idle", nav.Phase);
    }
}
