using System.Text.RegularExpressions;
using AlleyCat.Rigging;
using AlleyCat.XR.HandTracking;
using Godot;
using Xunit;

namespace AlleyCat.Tests.XR;

/// <summary>
/// Textual contract tests of the two shipped grab-pose reference assets against the generic hand-pose
/// reference sampler's track contract (XR-002 TR52; INTR-001 TR16): the immutable single-frame Grab-ball-40
/// and Grab-pipe-10 resources are parsed directly from their serialised text — no Godot runtime — to pin that
/// each side's 15 canonical finger destinations resolves exactly one enabled single-key <c>Rotation3D</c>
/// track at <c>t=0</c> with a valid quaternion, the exact rule the runtime sampler enforces.
/// </summary>
public sealed class AuthoredHandPoseReferenceSamplerAssetTests
{
    private const string GrabBallPath = "res://assets/characters/reference/female/animations/Grab-ball-40.tres";

    private const string GrabPipePath = "res://assets/characters/reference/female/animations/Grab-pipe-10.tres";

    private const float QuaternionUnitTolerance = 0.001f;

    private static readonly string[] _grabReferences = [GrabBallPath, GrabPipePath];

    /// <summary>Both shipped grab references carry every side's 15 canonical finger rotation tracks exactly once.</summary>
    [Fact]
    public void BothShippedGrabReferences_ResolveEveryFingerTrackExactlyOncePerSide()
    {
        foreach (string resourcePath in _grabReferences)
        {
            AuthoredReferenceText resource = ParseResource(resourcePath);

            foreach (LimbSide side in new[] { LimbSide.Left, LimbSide.Right })
            {
                foreach (XRHandJoint joint in XRHandJoints.DestinationJoints)
                {
                    string boneName = AuthoredHandPoseSideReference.GetCanonicalBoneName(side, joint);
                    int matches = resource.RotationTracks(boneName).Count();

                    Assert.True(
                        matches == 1,
                        $"{resourcePath}:{boneName} must resolve exactly one enabled Rotation3D track; got {matches}.");

                    // The path is the exact canonical form with no fuzzy matching (XR-002 TR52, TR25.3-generalised).
                    Assert.Equal(
                        AuthoredThumbReferenceSampler.TrackPathPrefix + boneName,
                        resource.RotationTracks(boneName).Single().Path);
                }
            }
        }
    }

    /// <summary>
    /// Every required finger track holds exactly one key at <c>t=0</c> with a finite quaternion satisfying
    /// <c>|length² − 1| ≤ 0.001</c> — the value contract the runtime sampler enforces fail-closed.
    /// </summary>
    [Fact]
    public void EveryRequiredFingerTrack_HoldsOneValidKeyAtTimeZero()
    {
        foreach (string resourcePath in _grabReferences)
        {
            AuthoredReferenceText resource = ParseResource(resourcePath);

            foreach (LimbSide side in new[] { LimbSide.Left, LimbSide.Right })
            {
                foreach (XRHandJoint joint in XRHandJoints.DestinationJoints)
                {
                    string boneName = AuthoredHandPoseSideReference.GetCanonicalBoneName(side, joint);
                    AuthoredTrackText track = resource.RotationTracks(boneName).Single();

                    Assert.True(track.Keys.Count == 1, $"{resourcePath}:{boneName} must hold exactly one key.");
                    Assert.Equal(0.0, track.Keys[0].Time, 6);

                    Quaternion quaternion = track.Keys[0].Rotation
                        ?? throw new InvalidOperationException($"No rotation key for {boneName}.");
                    Assert.True(
                        float.IsFinite(quaternion.X) && float.IsFinite(quaternion.Y)
                        && float.IsFinite(quaternion.Z) && float.IsFinite(quaternion.W),
                        $"{resourcePath}:{boneName} key must be finite.");
                    Assert.True(
                        Math.Abs(quaternion.LengthSquared() - 1.0f) <= QuaternionUnitTolerance,
                        $"{resourcePath}:{boneName} key must satisfy |length² − 1| ≤ {QuaternionUnitTolerance}; got " +
                        $"{quaternion.LengthSquared():R}.");
                }
            }
        }
    }

    /// <summary>
    /// The grab references genuinely articulate: at least the thumb and one non-thumb chain exceed the power-grip
    /// minimum reference angle on both sides, so the shipped candidates can derive a recognition profile.
    /// </summary>
    [Fact]
    public void BothShippedGrabReferences_ArticulateBeyondTheNeutral()
    {
        foreach (string resourcePath in _grabReferences)
        {
            AuthoredReferenceText resource = ParseResource(resourcePath);

            foreach (LimbSide side in new[] { LimbSide.Left, LimbSide.Right })
            {
                float maximumArticulation = 0.0f;
                foreach (XRHandJoint joint in XRHandJoints.DestinationJoints)
                {
                    string boneName = AuthoredHandPoseSideReference.GetCanonicalBoneName(side, joint);
                    Quaternion key = resource.RotationTracks(boneName).Single().Keys[0].Rotation!.Value;
                    maximumArticulation = MathF.Max(maximumArticulation, 2.0f * MathF.Atan2(
                        new Vector3(key.X, key.Y, key.Z).Length(),
                        key.W));
                }

                Assert.True(
                    maximumArticulation > Mathf.DegToRad(20.0f),
                    $"{resourcePath} {side} hand must articulate beyond 20° somewhere for grip recognition.");
            }
        }
    }

    private sealed record AuthoredReferenceText(float Length, List<AuthoredTrackText> Tracks)
    {
        public IEnumerable<AuthoredTrackText> RotationTracks(string bone)
            => Tracks.Where(track =>
                track.Type == "rotation_3d"
                && track.Enabled
                && track.Path == AuthoredThumbReferenceSampler.TrackPathPrefix + bone);
    }

    private sealed record AuthoredTrackText(int Index, string Type, bool Enabled, string Path, List<AuthoredKeyText> Keys);

    private sealed record AuthoredKeyText(double Time, Quaternion? Rotation);

    private static AuthoredReferenceText ParseResource(string resourcePath)
    {
        string fullPath = ResolveAssetPath(resourcePath);
        string[] lines = File.ReadAllLines(fullPath);

        float length = 0.0f;
        Dictionary<int, (string? Type, bool? Enabled, string? Path, List<float>? Keys)> tracks = [];

        foreach (string line in lines)
        {
            Match match = Regex.Match(line, @"^length = ([0-9.eE+-]+)");
            if (match.Success)
            {
                length = float.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                continue;
            }

            match = Regex.Match(line, @"^tracks/(\d+)/type = ""([^""]+)""");
            if (match.Success)
            {
                tracks[int.Parse(match.Groups[1].Value)] = (match.Groups[2].Value, null, null, null);
                continue;
            }

            match = Regex.Match(line, @"^tracks/(\d+)/enabled = (true|false)");
            if (match.Success)
            {
                int enabledIndex = int.Parse(match.Groups[1].Value);
                (string? Type, bool? Enabled, string? Path, List<float>? Keys) enabledEntry = tracks[enabledIndex];
                tracks[enabledIndex] =
                    (enabledEntry.Type, bool.Parse(match.Groups[2].Value), enabledEntry.Path, enabledEntry.Keys);
                continue;
            }

            match = Regex.Match(line, @"^tracks/(\d+)/path = NodePath\(""([^""]+)""\)");
            if (match.Success)
            {
                int pathIndex = int.Parse(match.Groups[1].Value);
                (string? Type, bool? Enabled, string? Path, List<float>? Keys) pathEntry = tracks[pathIndex];
                tracks[pathIndex] =
                    (pathEntry.Type, pathEntry.Enabled, match.Groups[2].Value, pathEntry.Keys);
                continue;
            }

            match = Regex.Match(line, @"^tracks/(\d+)/keys = PackedFloat32Array\(([^)]*)\)");
            if (match.Success)
            {
                int keysIndex = int.Parse(match.Groups[1].Value);
                (string? Type, bool? Enabled, string? Path, List<float>? Keys) keysEntry = tracks[keysIndex];
                List<float> values =
                [
                    .. match.Groups[2].Value
                        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                        .Select(value => float.Parse(value, System.Globalization.CultureInfo.InvariantCulture)),
                ];
                tracks[keysIndex] = (keysEntry.Type, keysEntry.Enabled, keysEntry.Path, values);
            }
        }

        List<AuthoredTrackText> parsed =
        [
            .. tracks.OrderBy(pair => pair.Key).Select(pair => new AuthoredTrackText(
                pair.Key,
                pair.Value.Type ?? throw new InvalidOperationException($"Track {pair.Key} has no type."),
                pair.Value.Enabled ?? throw new InvalidOperationException($"Track {pair.Key} has no enabled flag."),
                pair.Value.Path ?? throw new InvalidOperationException($"Track {pair.Key} has no path."),
                ParseKeys(pair.Value.Type!, pair.Value.Keys ?? []))),
        ];
        return new AuthoredReferenceText(length, parsed);
    }

    private static List<AuthoredKeyText> ParseKeys(string type, List<float> values)
    {
        // Rotation3D keys are (time, transition, x, y, z, w); position/scale keys are (time, transition, x, y, z).
        int stride = type == "rotation_3d" ? 6 : 5;
        Assert.True(
            values.Count % stride == 0,
            $"Track key payload {values.Count} must divide by the {type} stride {stride}.");

        List<AuthoredKeyText> keys = [];
        for (int offset = 0; offset < values.Count; offset += stride)
        {
            Quaternion? rotation = type == "rotation_3d"
                ? new Quaternion(values[offset + 2], values[offset + 3], values[offset + 4], values[offset + 5])
                : null;
            keys.Add(new AuthoredKeyText(values[offset], rotation));
        }

        return keys;
    }

    private static string ResolveAssetPath(string resourcePath)
    {
        string relative = "game/" + resourcePath["res://".Length..];
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate {resourcePath} from the test output directory.");
    }
}
