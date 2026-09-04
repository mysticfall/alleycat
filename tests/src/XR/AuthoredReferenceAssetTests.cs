using System.Text.RegularExpressions;
using AlleyCat.XR.HandTracking;
using Godot;
using Xunit;

namespace AlleyCat.Tests.XR;

/// <summary>
/// Textual contract tests of the two real authored reference assets (XR-002 TR25, A12): the immutable
/// single-frame Reset and Grab-pipe-10 resources are parsed directly from their serialised text — no Godot
/// runtime — to pin the static-pose layout (resource identity, length, single key per track at t=0, exact
/// thumb track paths, the co-resident ThumbProximal scale track, and the pinned key values), and the parsed
/// keys are then fed through the pure authored-axis maths to reproduce the pinned oracle.
/// </summary>
public sealed class AuthoredReferenceAssetTests
{
    private const string NeutralPath = "res://assets/characters/reference/female/animations/Reset.tres";

    private const string FlexionPath = "res://assets/characters/reference/female/animations/Grab-pipe-10.tres";

    private const float AngularEpsilonDegrees = 0.1f;

    private static readonly string[] _thumbBones =
    [
        "LeftThumbMetacarpal",
        "LeftThumbProximal",
        "LeftThumbDistal",
        "RightThumbMetacarpal",
        "RightThumbProximal",
        "RightThumbDistal",
    ];

    /// <summary>
    /// Both references are the pinned 57-track static single-key poses of length 0.041666668 s with every
    /// enabled track holding exactly one key at t=0 (XR-002 TR25).
    /// </summary>
    [Fact]
    public void BothReferences_AreStaticSingleKeyPoses()
    {
        AuthoredReferenceText neutral = ParseResource(NeutralPath);
        AuthoredReferenceText flexion = ParseResource(FlexionPath);

        Assert.Equal("Reset", neutral.ResourceName);
        Assert.Equal("Grab-pipe-10", flexion.ResourceName);
        Assert.Equal(0.041666668f, neutral.Length, 6);
        Assert.Equal(0.041666668f, flexion.Length, 6);
        Assert.Equal(57, neutral.Tracks.Count);
        Assert.Equal(57, flexion.Tracks.Count);

        foreach (AuthoredReferenceText resource in new[] { neutral, flexion })
        {
            foreach (AuthoredTrackText track in resource.Tracks)
            {
                Assert.True(track.Enabled, $"Track {track.Path} must be enabled.");
                Assert.True(track.Keys.Count == 1, $"Track {track.Path} must hold exactly one key.");
                Assert.Equal(0.0, track.Keys[0].Time, 6);
            }
        }
    }

    /// <summary>
    /// Each of the six thumb bones resolves exactly one enabled Rotation3D track at the exact canonical path in
    /// both references, and the co-resident ThumbProximal scale tracks are excluded by that rule (XR-002
    /// TR25.3, A12).
    /// </summary>
    [Fact]
    public void ThumbRotationTracks_ResolveExactlyOncePerBone()
    {
        AuthoredReferenceText neutral = ParseResource(NeutralPath);
        AuthoredReferenceText flexion = ParseResource(FlexionPath);

        foreach (string bone in _thumbBones)
        {
            Assert.True(
                neutral.RotationTracks(bone).Count() == 1,
                $"{NeutralPath}:{bone} must resolve exactly one enabled Rotation3D track.");
            Assert.True(
                flexion.RotationTracks(bone).Count() == 1,
                $"{FlexionPath}:{bone} must resolve exactly one enabled Rotation3D track.");

            // The path is the exact canonical form with no fuzzy matching (XR-002 TR25.3).
            Assert.Equal(
                AuthoredThumbReferenceSampler.TrackPathPrefix + bone,
                neutral.RotationTracks(bone).Single().Path);
            Assert.Equal(
                AuthoredThumbReferenceSampler.TrackPathPrefix + bone,
                flexion.RotationTracks(bone).Single().Path);
        }

        // The co-resident ThumbProximal scale track — one per side — is excluded by the
        // exactly-one-enabled-Rotation3D rule (XR-002 TR25.3).
        Assert.Equal(2, neutral.Tracks.Count(track =>
            track.Type == "scale_3d" && track.Path.EndsWith("ThumbProximal", StringComparison.Ordinal)));
        Assert.Equal(2, flexion.Tracks.Count(track =>
            track.Type == "scale_3d" && track.Path.EndsWith("ThumbProximal", StringComparison.Ordinal)));
    }

    /// <summary>
    /// The thumb key values match the pinned asset facts: the ≈95.26-degree metacarpal Reset keys, the
    /// identity proximal/distal Reset keys, and the soft-fist flexion keys (XR-002 TR25).
    /// </summary>
    [Fact]
    public void ThumbKeyValues_MatchThePinnedAssetFacts()
    {
        AuthoredReferenceText neutral = ParseResource(NeutralPath);
        AuthoredReferenceText flexion = ParseResource(FlexionPath);

        AssertQuaternionApproximately(
            new Quaternion(-0.2141868f, 0.6738872f, 0.21418674f, 0.6738873f),
            neutral.ThumbKey("LeftThumbMetacarpal"));
        AssertQuaternionApproximately(
            new Quaternion(0.2141868f, 0.6738872f, 0.21418674f, -0.6738873f),
            neutral.ThumbKey("RightThumbMetacarpal"));
        AssertQuaternionApproximately(
            new Quaternion(-0.16021137f, 0.75925297f, 0.30426446f, 0.5525308f),
            flexion.ThumbKey("LeftThumbMetacarpal"));
        AssertQuaternionApproximately(
            new Quaternion(0.16014078f, 0.11171712f, 0.061266482f, 0.9788364f),
            flexion.ThumbKey("LeftThumbProximal"));
        AssertQuaternionApproximately(
            new Quaternion(0.23050137f, 0.14565916f, 0.12815726f, 0.9535346f),
            flexion.ThumbKey("LeftThumbDistal"));

        // The proximal/distal Reset keys are identity within float storage noise (XR-002 TR25).
        AssertQuaternionApproximately(
            Quaternion.Identity,
            neutral.ThumbKey("LeftThumbProximal"));
        AssertQuaternionApproximately(
            Quaternion.Identity,
            neutral.ThumbKey("LeftThumbDistal"));
        AssertQuaternionApproximately(
            Quaternion.Identity,
            neutral.ThumbKey("RightThumbProximal"));
        AssertQuaternionApproximately(
            Quaternion.Identity,
            neutral.ThumbKey("RightThumbDistal"));
    }

    /// <summary>
    /// Every right thumb key mirrors its left counterpart as (x, -y, -z, w) modulo the quaternion double-cover
    /// storage sign on the metacarpal (XR-002 TR25).
    /// </summary>
    [Fact]
    public void RightKeys_MirrorLeftValues()
    {
        AuthoredReferenceText neutral = ParseResource(NeutralPath);
        AuthoredReferenceText flexion = ParseResource(FlexionPath);

        foreach (string bone in _thumbBones)
        {
            string suffix = bone.StartsWith("Left", StringComparison.Ordinal)
                ? bone["Left".Length..]
                : bone["Right".Length..];
            foreach ((AuthoredReferenceText Resource, string Path) resource in new[]
                     {
                         (neutral, NeutralPath),
                         (flexion, FlexionPath),
                     })
            {
                Quaternion left = resource.Resource.ThumbKey("Left" + suffix);
                Quaternion right = resource.Resource.ThumbKey("Right" + suffix);

                // The right stored value is (x, -y, -z, w) of the left, modulo the double-cover storage sign
                // on the thumb metacarpal (XR-002 TR25).
                Quaternion mirrored = new(left.X, -left.Y, -left.Z, left.W);
                float dot = (mirrored.X * right.X) + (mirrored.Y * right.Y) + (mirrored.Z * right.Z)
                    + (mirrored.W * right.W);
                Assert.True(
                    dot is > 0.999f or < -0.999f,
                    $"{resource.Path}:{bone} right key must mirror the left key modulo the quaternion " +
                    $"double cover; dot {dot:R}.");
            }
        }
    }

    /// <summary>
    /// Feeding the textually parsed real keys through the pure authored-axis maths reproduces the pinned
    /// six-axis oracle within 0.1 degrees on both sides (XR-002 TR26, A10).
    /// </summary>
    [Fact]
    public void ParsedKeys_ThroughThePureMaths_ReproduceThePinnedOracle()
    {
        AuthoredReferenceText neutral = ParseResource(NeutralPath);
        AuthoredReferenceText flexion = ParseResource(FlexionPath);

        foreach (bool mirrored in new[] { true, false })
        {
            string prefix = mirrored ? "Left" : "Right";
            foreach ((string Bone, Vector3 ExpectedAxis, float ExpectedAngle) in new[]
                     {
                         ("ThumbMetacarpal", AuthoredThumbReferenceOracles.MetacarpalAxis(mirrored),
                             AuthoredThumbReferenceOracles.MetacarpalAngleDegrees),
                         ("ThumbProximal", AuthoredThumbReferenceOracles.ProximalAxis(mirrored),
                             AuthoredThumbReferenceOracles.ProximalAngleDegrees),
                         ("ThumbDistal", AuthoredThumbReferenceOracles.DistalAxis(mirrored),
                             AuthoredThumbReferenceOracles.DistalAngleDegrees),
                     })
            {
                Assert.True(AuthoredThumbAxisMath.TryDeriveAuthoredAxis(
                    neutral.ThumbKey(prefix + Bone),
                    flexion.ThumbKey(prefix + Bone),
                    $"{prefix} {Bone}",
                    out AuthoredThumbAxis axis,
                    out string error), error);
                Assert.True(
                    axis.Axis.Normalized().AngleTo(ExpectedAxis.Normalized()) <= Mathf.DegToRad(AngularEpsilonDegrees),
                    $"{prefix} {Bone}: expected axis {ExpectedAxis}, got {axis.Axis}.");
                Assert.True(Mathf.Abs(axis.ReferenceAngleDegrees - ExpectedAngle) <= AngularEpsilonDegrees,
                    $"{prefix} {Bone}: expected angle {ExpectedAngle}, got {axis.ReferenceAngleDegrees}.");
            }
        }
    }

    private sealed record AuthoredReferenceText(string ResourceName, float Length, List<AuthoredTrackText> Tracks)
    {
        public IEnumerable<AuthoredTrackText> RotationTracks(string bone)
            => Tracks.Where(track =>
                track.Type == "rotation_3d"
                && track.Enabled
                && track.Path == AuthoredThumbReferenceSampler.TrackPathPrefix + bone);

        public Quaternion ThumbKey(string bone)
            => RotationTracks(bone).Single().Keys[0].Rotation
                ?? throw new InvalidOperationException($"No rotation key for {bone}.");
    }

    private sealed record AuthoredTrackText(int Index, string Type, bool Enabled, string Path, List<AuthoredKeyText> Keys);

    private sealed record AuthoredKeyText(double Time, Quaternion? Rotation);

    private static AuthoredReferenceText ParseResource(string resourcePath)
    {
        string fullPath = ResolveAssetPath(resourcePath);
        string[] lines = File.ReadAllLines(fullPath);

        string resourceName = string.Empty;
        float length = 0.0f;
        Dictionary<int, (string? Type, bool? Enabled, string? Path, List<float>? Keys)> tracks = [];

        foreach (string line in lines)
        {
            Match match = Regex.Match(line, @"^resource_name = ""([^""]+)""");
            if (match.Success)
            {
                resourceName = match.Groups[1].Value;
                continue;
            }

            match = Regex.Match(line, @"^length = ([0-9.eE+-]+)");
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
        return new AuthoredReferenceText(resourceName, length, parsed);
    }

    private static List<AuthoredKeyText> ParseKeys(string type, List<float> values)
    {
        // Rotation3D keys are (time, transition, x, y, z, w); position keys are (time, transition, x, y, z);
        // scale keys are (time, transition, x, y, z).
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

    private static void AssertQuaternionApproximately(Quaternion expected, Quaternion actual)
        => Assert.True(
            expected.Normalized().AngleTo(actual.Normalized()) <= Mathf.DegToRad(AngularEpsilonDegrees),
            $"Expected quaternion within {AngularEpsilonDegrees} degrees of {expected}, got {actual}.");
}
