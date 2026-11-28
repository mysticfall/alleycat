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
            string path = Path.Combine(directory.FullName, "game/assets/animations/packs/default/locomotion/feminine");
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

    /// <summary>Two primary source lifts per cycle; intensity changes only the next cycle's angular allocation.</summary>
    [Theory]
    [InlineData("left_turn", 947, 961, 4.47112647, 38.89189769, 66.841682)]
    [InlineData("right_turn", 1103, 1120, -20.90773399, -82.32564080, -91.351919)]
    public void MappedLifts_UseMeasuredAnglesAndLatchMagnitude(string role, int first, int second,
        double angle1, double angle2, double span)
    {
        var turn = new MetadataStationaryTurn(Reference(role), .17);
        MetadataStationaryTurn.Snapshot a = turn.Select(1);
        Assert.Equal(first, a.WindowStart);
        Assert.InRange(Math.Abs(((a.RootYaw - .17) * 180 / Math.PI) - (120 * angle1 / Math.Abs(span))), 0, .002);
        Assert.True(turn.Complete(a));
        MetadataStationaryTurn.Snapshot b = turn.Select(.4);
        Assert.Equal(second, b.WindowStart);
        Assert.Equal(1, b.Magnitude);
        Assert.InRange(Math.Abs(((b.RootYaw - .17) * 180 / Math.PI) - (120 * angle2 / Math.Abs(span))), 0, .002);
        Assert.False(turn.Complete(a));
        Assert.True(turn.Complete(b));
        MetadataStationaryTurn.Snapshot c = turn.Select(.4);
        Assert.Equal(first + 60, c.WindowStart);
        Assert.Equal(Math.Sign(span) * 48, c.Allocation * 180 / Math.PI, 8);
        Assert.Equal(Math.Sign(span) * 120, (c.CycleStart - .17) * 180 / Math.PI, 8);
        turn.Retire();
        Assert.Null(turn.Owner);
        Assert.False(turn.Complete(c));
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
