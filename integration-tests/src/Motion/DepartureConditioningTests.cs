using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AlleyCat.Motion;
using AlleyCat.Navigation;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

/// <summary>Frozen target-space conditioning, never a saved-response or foot-naturalness oracle.</summary>
public sealed class DepartureConditioningTests
{
    private const double Horizon = 49.0 / 30;
    private const string Evidence = "res://temp/motionbricks/departure-conditioning-01/";
    private const string Archive = "res://temp/motionbricks/native-path-stride-regression-01/";

    /// <summary>The exact archived first input exposes non-forward suffix tension; only its magnitude is conditioned.</summary>
    [Headless]
    [Fact]
    public void FrozenFirstWindowShortensAdvanceAndMatchingSuffixOnly()
    {
        byte[] bytes = File.ReadAllBytes(ProjectSettings.GlobalizePath(Archive + "first-window.f32"));
        Assert.Equal(7392 * sizeof(float), bytes.Length);
        float[] values = new float[7392];
        Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
        Transform3D[][] poses = [.. Enumerable.Range(0, 8).Select(f => Enumerable.Range(0, 77)
            .Select(j => Decode(values, ((f * 77) + j) * 12)).ToArray())];
        Transform3D[][] archived = poses[4..];
        using var metadata = JsonDocument.Parse(File.ReadAllText(ProjectSettings.GlobalizePath(Archive + "first-window.json")));
        JsonElement intent = metadata.RootElement.GetProperty("event").GetProperty("data").GetProperty("intent");
        float heading = intent.GetProperty("Heading").GetSingle(), speed = intent.GetProperty("Speed").GetSingle();
        Vector3 start = Ground(SomaMotionData.ModelToWorld * poses[3][0].Origin);
        Vector3 direction = Ground(World(archived[1]) - World(archived[0])).Normalized();
        // Successful native path arrays were not recorded. This is an explicitly labelled straight-edge fixture
        // constrained by the archived station/suffix, not a claimed reconstruction of the full native route.
        Vector3[] path = [start, start + (direction * 10)];
        float archivedAdvance = Ground(World(archived[0]) - start).Length();
        Vector3 archivedVelocity = Ground(World(archived[1]) - World(archived[0])) * 30;
        float archivedFacing = intent.GetProperty("EncodedHeadings")[0].GetSingle();
        float forward = archivedVelocity.Dot(new(Mathf.Sin(archivedFacing), 0, Mathf.Cos(archivedFacing)));
        float left = archivedVelocity.Dot(new(Mathf.Cos(archivedFacing), 0, -Mathf.Sin(archivedFacing)));
        Assert.InRange(archivedAdvance, .94482f, .94485f);
        Assert.InRange(forward, -.07464f, -.07460f);
        Assert.InRange(left, .57361f, .57366f);

        using ReachabilityOnlyNavigation nav = CreateNavigation();
        Transform3D[][] target = Clone(archived);
        MotionBricksNavigation.CurveIntent result = nav.PlaceNativeTravelTargets(target, path, 1, start, heading, speed);
        using var angular = JsonDocument.Parse(File.ReadAllText(ProjectSettings.GlobalizePath(
            "res://data/motionbricks/reviewed-feminine/turn-angular-reference.json")));
        float intendedTurn = Mathf.Wrap(Mathf.Atan2(direction.X, direction.Z) - heading, -Mathf.Pi, Mathf.Pi);
        double currentRate = Math.Abs(angular.RootElement.GetProperty(intendedTurn > 0 ? "left_turn" : "right_turn")
            .GetProperty("representativeAngularRateRadiansPerSecond").GetDouble());
        float advance = Ground(World(target[0]) - start).Length();
        Vector3 suffix = Ground(World(target[1]) - World(target[0])) * 30;
        File.WriteAllText(ProjectSettings.GlobalizePath(Evidence + "latest-targets.json"), JsonSerializer.Serialize(new
        {
            archiveSHA256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            archivedAdvance,
            forward,
            left,
            advance,
            suffixSpeed = suffix.Length(),
            stationRatio = advance / archivedAdvance,
            suffixRatio = suffix.Length() / archivedVelocity.Length(),
            result.EncodedHeadings,
            positions = target.Select(p => new[] { World(p).X, World(p).Y, World(p).Z }),
            scope = "Production placement / archived-input straight-edge component fixture; no worker or corrected future"
        }, new JsonSerializerOptions { WriteIndented = true }));

        // Authority: explicitly approved magnitude-only first-window criterion. No foot-width/quality bound.
        Assert.True(advance > 0 && advance < archivedAdvance, $"First advance was not shortened: {advance}");
        double metadataFraction = Math.Abs(intent.GetProperty("AngularRate").GetDouble()) * Horizon /
            Math.Abs(intent.GetProperty("GoalError").GetDouble());
        Assert.InRange(advance / archivedAdvance, 0, metadataFraction + .00002);
        Assert.InRange(Math.Abs((suffix.Length() / archivedVelocity.Length()) - (advance / archivedAdvance)), 0, .00002f);
        Assert.InRange(suffix.Normalized().DistanceTo(direction), 0, .00002f);
        float lastStation = 0;
        for (int i = 0; i < 4; i++)
        {
            Vector3 delta = Ground(World(target[i]) - start);
            float station = delta.Dot(direction);
            Assert.True(station > lastStation);
            Assert.InRange((delta - (direction * station)).Length(), 0, .000002f);
            float progress = Mathf.Wrap(result.EncodedHeadings[i] - heading, -Mathf.Pi, Mathf.Pi);
            Assert.Equal(Math.Sign(intendedTurn), Math.Sign(progress));
            Assert.InRange(Math.Abs(progress),
                0, (float)(currentRate * (Horizon + (i / 30.0))) + .000002f);
            Assert.Equal(archived[i][0].Origin.Y, target[i][0].Origin.Y);
            // Root yaw follows the current source's angular budget, not the archived take's rate.
            // Yaw-only placement preserves each source axis's vertical component and length.
            for (int axis = 0; axis < 3; axis++)
            {
                Assert.Equal(archived[i][0].Basis[axis].Y, target[i][0].Basis[axis].Y, .000002f);
                Assert.Equal(archived[i][0].Basis[axis].Length(), target[i][0].Basis[axis].Length(), .000002f);
            }
            Assert.Equal(archived[i][1..], target[i][1..]);
            lastStation = station;
        }
        // Subsequent windows are ordinary native distance-ahead placement, even if the supplied fixture is still rearward.
        Transform3D[][] subsequent = Clone(archived);
        _ = nav.PlaceNativeTravelTargets(subsequent, path, 1, start, heading, speed);
        Assert.InRange(Math.Abs(Ground(World(subsequent[0]) - start).Length() - (speed * (float)Horizon)), 0, .000002f);
    }

    /// <summary>Compatible departures and later long-straight windows retain bit-exact placement, source bones and pacing.</summary>
    [Headless]
    [Fact]
    public void CompatibleDepartureAndLongStraightKeepOriginalPlacement()
    {
        using ReachabilityOnlyNavigation nav = CreateNavigation();
        var data = new SomaMotionData(ProjectSettings.GlobalizePath("res://data/motionbricks/poses.json"));
        var sampler = new ReviewedRoleSampler(ProjectSettings.GlobalizePath("res://data/motionbricks/reviewed-feminine/set.json"), data);
        _ = sampler.SetIntent("forward", 0);
        const float heading = 0;
        Vector3[] path = [Vector3.Zero, new(0, 0, 100)];
        for (int window = 0; window < 3; window++)
        {
            Transform3D[][] source = sampler.Targets(window, Horizon, Basis.Identity, Vector3.Zero);
            Transform3D[][] expected = Clone(source), actual = Clone(source);
            for (int i = 0; i < 4; i++)
            {
                // Independent old straight-path contract: reference speed * horizon, untouched original facing formula.
                float time = (float)(Horizon + (i / 30.0));
                int right = Array.IndexOf(data.Names, "RightLeg"), left = Array.IndexOf(data.Names, "LeftLeg");
                float sourceHeading = SomaInference.Heading(expected[i][0] * expected[i][right].Origin, expected[i][0] * expected[i][left].Origin);
                float error = Mathf.Wrap(Mathf.Atan2(0, 1) - heading, -Mathf.Pi, Mathf.Pi);
                float rate = Math.Abs(sampler.TurnReferenceAngularVelocity(error > 0));
                float originalHeading = heading + (float)Math.Clamp(error, -rate * (Horizon + (i / 30.0)), rate * (Horizon + (i / 30.0)));
                expected[i][0].Basis = new Basis(Vector3.Up, originalHeading - Mathf.Pi - sourceHeading) * expected[i][0].Basis;
                Vector3 position = SomaMotionData.ModelToWorld.Inverse() * new Vector3(0, 0, sampler.ForwardReferenceSpeed * time);
                position.Y += expected[i][0].Origin.Y;
                expected[i][0].Origin = position;
            }
            _ = nav.PlaceNativeTravelTargets(actual, path, 1, Vector3.Zero, heading, sampler.ForwardReferenceSpeed);
            for (int i = 0; i < 4; i++)
            {
                Assert.Equal(expected[i], actual[i]);
            }
        }
    }

    private static ReachabilityOnlyNavigation CreateNavigation()
    {
        var nav = new ReachabilityOnlyNavigation();
        var data = new SomaMotionData(ProjectSettings.GlobalizePath("res://data/motionbricks/poses.json"));
        var sampler = new ReviewedRoleSampler(ProjectSettings.GlobalizePath("res://data/motionbricks/reviewed-feminine/set.json"), data);
        typeof(MotionBricksNavigation).GetField("_data", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(nav, data);
        typeof(MotionBricksNavigation).GetField("_reviewed", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(nav, sampler);
        return nav;
    }

    private static Transform3D[][] Clone(Transform3D[][] poses) => [.. poses.Select(p => (Transform3D[])p.Clone())];
    private static Vector3 World(Transform3D[] pose) => SomaMotionData.ModelToWorld * pose[0].Origin;
    private static Vector3 Ground(Vector3 value) => new(value.X, 0, value.Z);
    private static Transform3D Decode(float[] v, int i) => new(new Basis(new(v[i], v[i + 1], v[i + 2]),
        new(v[i + 3], v[i + 4], v[i + 5]), new(v[i + 6], v[i + 7], v[i + 8])), new(v[i + 9], v[i + 10], v[i + 11]));
}
