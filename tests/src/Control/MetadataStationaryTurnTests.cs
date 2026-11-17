using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using AlleyCat.Control;
using AlleyCat.Motion;
using Xunit;

namespace AlleyCat.Tests.Control;

/// <summary>Normal source-landmark policy; synthetic root endpoints, never generated-motion acceptance.</summary>
public sealed class MetadataStationaryTurnTests
{
    private static readonly string _directoryPath = FindData();

    private static string FindData()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "game/data/motionbricks/reviewed-feminine");
            if (File.Exists(Path.Combine(path, "landing-ablation-01.json")))
            {
                return path;
            }
        }
        throw new DirectoryNotFoundException("Shared source unavailable.");
    }

    private static StationaryTurnReference Reference(string role = "left_turn", Action<JsonObject>? change = null)
    {
        JsonObject metadata = JsonNode.Parse(File.ReadAllText(Path.Combine(_directoryPath, "turn-angular-reference.json")))![role]!.AsObject();
        change?.Invoke(metadata);
        string path = Path.Combine(_directoryPath, "landing-ablation-01.json");
        using var source = JsonDocument.Parse(File.ReadAllText(path));
        using var document = JsonDocument.Parse(metadata.ToJsonString());
        return new StationaryTurnReference(role, document.RootElement, source.RootElement,
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))),
            role == "left_turn" ? 946 : 1096, role == "left_turn" ? 1006 : 1156);
    }

    /// <summary>Approved frames and independently exported Hips angles service both signs in chronological order.</summary>
    [Fact]
    public void BothSigns_ServiceAllFourLandmarksWithoutChangingSourceClock()
    {
        (string Role, int[] Frames, double[] Degrees)[] cases = [
            ("left_turn", [947, 951, 961, 1001], [4.471126472374838, 18.00060249623247, 38.89189768944148, 68.57880861799906]),
            ("right_turn", [1103, 1107, 1120, 1152], [-20.907733993342532, -39.10439950786896, -82.32564079780614, -91.36945268112389])];
        foreach ((string role, int[] frames, double[] degrees) in cases)
        {
            var turn = new MetadataStationaryTurn(Reference(role), 0);
            double actor = 0;
            for (int i = 0; i < 4; i++)
            {
                turn.Advance(2, 1); // Elapsed debt cannot skip the current group's unserved poses.
                MetadataStationaryTurn.Snapshot selected = turn.Select(1, 49.0 / 30, actor);
                Assert.Equal(frames[i], selected.WindowStart);
                Assert.Equal(i % 2 == 0, selected.IsPrimaryLift);
                Assert.Equal(degrees[i], selected.RootYaw * 180 / Math.PI, 6);
                Assert.InRange(Math.Abs(selected.RootYaw - actor), 0, selected.Cap + 1e-10);
                actor = selected.RootYaw;
                Assert.True(turn.Complete(selected, actor));
                Assert.False(turn.Complete(selected, actor));
                Assert.Equal(60 * (i + 1), turn.Phase, 8);
            }
            Assert.Equal(2, turn.PrimaryLiftCompletions);
            Assert.Equal(2, turn.RecoveryCompletions);
            Assert.True(turn.DroppedCycles > 0);
        }
    }

    /// <summary>Prospective magnitude integration, immutable pending owners and stale-completion refusal.</summary>
    [Fact]
    public void IntensityAndRetirement_PreserveClockFloorAndOwnerLineage()
    {
        var turn = new MetadataStationaryTurn(Reference(), 0);
        turn.Advance(1, 1);
        MetadataStationaryTurn.Snapshot old = turn.Select(1, 49.0 / 30, 0);
        turn.Advance(2, .5);
        Assert.Equal(60, turn.Phase, 8);
        Assert.Same(old, turn.Owner);
        Assert.Equal(1, old.Magnitude);
        turn.Retire();
        Assert.False(turn.Complete(old, old.RootYaw));
        MetadataStationaryTurn.Snapshot replacement = turn.Select(.2, 49.0 / 30, 0);
        Assert.NotEqual(old.Epoch, replacement.Epoch);
        Assert.True(replacement.SelectedPhase >= old.SelectedPhase);
        Assert.Equal(.2, replacement.Magnitude);
        Assert.InRange(Math.Abs(replacement.RootYaw), 0, replacement.Cap + 1e-10);
        turn.Advance(0, 0);
        Assert.Equal(60, turn.Phase, 8);
    }

    /// <summary>Tracking overtake advances selection, never elapsed clock or backwards catch-up.</summary>
    [Fact]
    public void Overtake_PassesUnplayedCuesWithBoundedSearchAndUnchangedClock()
    {
        var turn = new MetadataStationaryTurn(Reference(), 0);
        MetadataStationaryTurn.Snapshot first = turn.Select(1, 49.0 / 30, 0);
        Assert.True(turn.Complete(first, first.RootYaw));
        turn.Advance(1, 1);
        double actual = 60 * Math.PI / 180;
        MetadataStationaryTurn.Snapshot next = turn.Select(.5, 49.0 / 30, actual);
        Assert.Equal(2, turn.PassedUnplayed);
        Assert.True(next.RootYaw >= actual);
        Assert.Equal(30, turn.Phase, 8);
        var exhausted = new MetadataStationaryTurn(Reference(), 0);
        for (int degrees = 150; degrees <= 600; degrees += 150)
        {
            _ = exhausted.TrackRoot(Math.IEEERemainder(degrees * Math.PI / 180, Math.Tau));
        }
        _ = Assert.Throws<InvalidOperationException>(() => exhausted.Select(1, 49.0 / 30, Math.IEEERemainder(600 * Math.PI / 180, Math.Tau)));
        Assert.Equal(8, exhausted.PassedUnplayed);
        Assert.Equal(0, exhausted.Phase);
        Assert.Null(exhausted.Owner);
    }

    /// <summary>When every forecast pose is behind actual tracking, select the next cue without repairing time.</summary>
    [Fact]
    public void OvertakeBeyondForecast_PreservesCapClockAndRetiredOwnerRefusal()
    {
        var turn = new MetadataStationaryTurn(Reference(), 0);
        MetadataStationaryTurn.Snapshot first = turn.Select(1, 49.0 / 30, 0);
        turn.Advance(29.0 / 30, 1);
        Assert.True(turn.Complete(first, first.RootYaw));
        MetadataStationaryTurn.Snapshot retired = turn.Select(1, 49.0 / 30, first.RootYaw);
        turn.Advance(1.0 / 30, 1);
        turn.Retire();
        double actual = 100 * Math.PI / 180;
        MetadataStationaryTurn.Snapshot next = turn.Select(.5, 49.0 / 30, actual);
        Assert.Equal(1021, next.WindowStart);
        Assert.Equal("RightFoot", next.Foot);
        Assert.Equal(54.5, next.ForecastPhase, 8);
        Assert.Equal(30, turn.Phase, 8);
        Assert.True(next.SelectedPhase > next.ForecastPhase);
        Assert.Equal(27.29368684700733, next.Cap * 180 / Math.PI, 6);
        Assert.Equal(105.73357976374514, next.RootYaw * 180 / Math.PI, 6);
        Assert.InRange(next.RootYaw - actual, 0, next.Cap);
        Assert.False(turn.Complete(retired, retired.RootYaw));
        Assert.Same(next, turn.Owner);
    }

    /// <summary>Authored overshoot and recovery reversal remain original geometry, not tracking overtake.</summary>
    [Fact]
    public void Recovery_AfterAuthoredOvershootRetainsNegativeIncrement()
    {
        var turn = new MetadataStationaryTurn(Reference(), 0);
        double actor = 0;
        for (int i = 0; i < 3; i++)
        {
            MetadataStationaryTurn.Snapshot selected = turn.Select(1, 49.0 / 30, actor);
            actor = selected.RootYaw;
            Assert.True(turn.Complete(selected, actor));
        }
        turn.Advance(17.0 / 30, 1);
        MetadataStationaryTurn.Snapshot interim = turn.Select(.5, 49.0 / 30, actor);
        Assert.Equal(987, interim.WindowStart);
        Assert.False(interim.IsLandmarkTarget);
        Assert.True(turn.Complete(interim, interim.RootYaw));
        MetadataStationaryTurn.Snapshot recovery = turn.Select(1, 49.0 / 30, interim.RootYaw);
        Assert.Equal(1001, recovery.WindowStart);
        Assert.True(recovery.RootYaw < interim.RootYaw);
        Assert.Equal(0, turn.PassedUnplayed);
        Assert.True(turn.Complete(recovery, recovery.RootYaw));
        Assert.Equal(2, turn.RecoveryCompletions);
    }

    /// <summary>Source/hash/bounds/typed landmark agreement is required before worker creation.</summary>
    [Fact]
    public void MetadataLoader_RejectsInvalidSourceAndLandmarkAssociations()
    {
        _ = Assert.Throws<InvalidDataException>(() => Reference(change: m => m["sourceBundleSHA256"] = "incorrect"));
        _ = Assert.Throws<InvalidDataException>(() => Reference(change: m => m["periodFrames"] = 61));
        _ = Assert.Throws<InvalidDataException>(() => Reference(change: m => m["representativeAngularRateRadiansPerSecond"] = 1));
        _ = Assert.Throws<InvalidDataException>(() => Reference(change: m => m["primaryFootEvents"]![1]!["foot"] = "LeftFoot"));
        _ = Assert.Throws<InvalidDataException>(() => Reference(change: m => m["primaryFootEvents"]![0]!["sourceAngularProgressRadians"] = .5));
        _ = Assert.Throws<InvalidDataException>(() => Reference(change: m => m["recoveryTrial"]!["sourceBundleSHA256"] = "incorrect"));
        _ = Assert.Throws<InvalidDataException>(() => Reference(change: m => m["recoveryTrial"]!["events"]![1]!["kind"] = "primary_lift"));
        _ = Assert.Throws<InvalidDataException>(() => Reference(change: m => m["recoveryTrial"]!["events"]![3]!["frame"] = 1005));
    }
}
