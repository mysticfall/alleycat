using System.Globalization;
using Xunit;

namespace AlleyCat.Tests.XR;

/// <summary>Text-level guards for the shipped optical-finger calibration resource.</summary>
public sealed class OpticalFingerCalibrationProfileResourceTests
{
    private const string SemanticProvenanceID = "reference-female-quest3-wivrn-authored-thumb-axes-v1";
    private const decimal SerialisationTolerance = 0.0000000005m;

    private static readonly IReadOnlyDictionary<(int Side, int Joint), decimal[]> _expectedSourceNeutrals =
        new Dictionary<(int Side, int Joint), decimal[]>
        {
            [(0, 1)] = [-0.079947028m, 0.5618452m, 0.402148143m, 0.718481256m],
            [(0, 2)] = [0.16066605m, -0.082452026m, -0.055896017m, 0.981969307m],
            [(0, 3)] = [-0.105460033m, 0.084218027m, 0.068658022m, 0.988469312m],
            [(0, 5)] = [0.183761645m, 0.020114948m, -0.109258861m, 0.976672692m],
            [(0, 6)] = [0.003242671m, -0.025568779m, -0.006858143m, 0.999644281m],
            [(0, 7)] = [-0.025892004m, -0.016465727m, -0.026737638m, 0.999171448m],
            [(0, 9)] = [0.261092265m, -0.026238159m, -0.081508925m, 0.961508546m],
            [(0, 10)] = [-0.001756608m, -0.011414507m, -0.004352703m, 0.999923836m],
            [(0, 11)] = [-0.059239228m, -0.037404361m, -0.002833273m, 0.997538771m],
            [(0, 13)] = [0.224532459m, -0.064318007m, -0.063592417m, 0.970259951m],
            [(0, 14)] = [0.005500336m, -0.033803354m, -0.002770696m, 0.999409527m],
            [(0, 15)] = [-0.009264074m, -0.003896694m, 0.029607039m, 0.999511088m],
            [(0, 17)] = [0.144475515m, 0.094214467m, 0.064075222m, 0.982926664m],
            [(0, 18)] = [0.015157515m, -0.040090018m, -0.042108551m, 0.998193323m],
            [(0, 19)] = [0.001357210m, 0.000917888m, 0.049402216m, 0.998777621m],
            [(1, 1)] = [-0.006335003m, -0.589948267m, -0.461939209m, 0.6622183m],
            [(1, 2)] = [0.245730042m, 0.081178014m, 0.047528008m, 0.964763165m],
            [(1, 3)] = [-0.043338015m, -0.083254029m, -0.063860022m, 0.993535344m],
            [(1, 5)] = [0.240030753m, -0.019368532m, 0.091644959m, 0.966235633m],
            [(1, 6)] = [0.004036845m, 0.025799219m, 0.007094232m, 0.999633821m],
            [(1, 7)] = [-0.027432040m, 0.016350422m, 0.026457625m, 0.999139700m],
            [(1, 9)] = [0.303707296m, 0.030253133m, 0.083450549m, 0.948621438m],
            [(1, 10)] = [-0.001689492m, 0.011366728m, 0.004200596m, 0.999925146m],
            [(1, 11)] = [-0.057345190m, 0.037602572m, 0.003317672m, 0.997640501m],
            [(1, 13)] = [0.272604421m, 0.067725959m, 0.063601906m, 0.957629794m],
            [(1, 14)] = [0.005467540m, 0.033466948m, 0.002876236m, 0.999420731m],
            [(1, 15)] = [-0.017550459m, 0.003737272m, -0.029237573m, 0.999411416m],
            [(1, 17)] = [0.227606673m, -0.092609343m, -0.043664544m, 0.968355369m],
            [(1, 18)] = [-0.012834835m, 0.037494415m, 0.043034932m, 0.998287248m],
            [(1, 19)] = [-0.010758433m, -0.001114397m, -0.049043396m, 0.998738084m],
        };

    private static readonly IReadOnlyDictionary<(int Side, int Joint), decimal[]> _expectedThumbDestinationNeutrals =
        new Dictionary<(int Side, int Joint), decimal[]>
        {
            [(0, 1)] = [-0.214186761m, 0.673887254m, 0.214186761m, 0.673887254m],
            [(0, 2)] = [0m, 0m, 0m, 1m],
            [(0, 3)] = [0m, 0m, 0m, 1m],
            [(1, 1)] = [-0.214186761m, -0.673887254m, -0.214186761m, 0.673887254m],
            [(1, 2)] = [0m, 0m, 0m, 1m],
            [(1, 3)] = [0m, 0m, 0m, 1m],
        };

    /// <summary>
    /// Guards the complete schema-2 30-record profile and its stable semantic provenance metadata.
    /// </summary>
    [Fact]
    public void ResourceContainsCompleteSchema2ProfileWithSemanticProvenance()
    {
        string resource = File.ReadAllText(ResolveRepositoryPath(
            "game",
            "assets",
            "xr",
            "calibration",
            "fingers_calibration_quest3.tres"));
        string entrySource = File.ReadAllText(ResolveRepositoryPath(
            "game",
            "src",
            "XR",
            "HandTracking",
            "OpticalFingerTrackingCalibrationEntry.cs"));
        string profileSource = File.ReadAllText(ResolveRepositoryPath(
            "game",
            "src",
            "XR",
            "HandTracking",
            "OpticalFingerTrackingCalibrationProfile.cs"));
        IReadOnlyList<string> entryBlocks = ReadEntryBlocks(resource);

        Assert.Equal(30, entryBlocks.Count);
        Assert.Contains("SchemaVersion = \"2\"", resource, StringComparison.Ordinal);
        Assert.Contains("ProfileVersion = \"quest3-wivrn-reference-female-v2\"", resource, StringComparison.Ordinal);
        Assert.Contains($"SourceCaptureID = \"{SemanticProvenanceID}\"", resource, StringComparison.Ordinal);
        Assert.Contains("Headset = \"Quest 3\"", resource, StringComparison.Ordinal);
        Assert.Contains("Runtime = \"WiVRn 26.6.2/OpenXR\"", resource, StringComparison.Ordinal);
        Assert.Contains("ReferenceRig = \"reference-female\"", resource, StringComparison.Ordinal);
        Assert.Contains("Provenance = \"Semantic profile provenance:", resource, StringComparison.Ordinal);
        Assert.Contains("string SourceCaptureID,\n    string Provenance);", profileSource, StringComparison.Ordinal);
        Assert.Contains("DestinationNeutral { get; set; } = Quaternion.Identity;", entrySource, StringComparison.Ordinal);
        Assert.Contains("BasisCorrespondence { get; set; } = Quaternion.Identity;", entrySource, StringComparison.Ordinal);
        Assert.Contains("Gain { get; set; } = 1.0f;", entrySource, StringComparison.Ordinal);

        AssertDecimalQuaternion(
            ReadQuaternionProperty(resource, "LeftMetacarpalNeutralAnchor"),
            [-0.1273963451385498m, 0.019958913326263428m, 0.25467225909233093m, 0.9583913087844849m]);
        AssertDecimalQuaternion(
            ReadQuaternionProperty(resource, "RightMetacarpalNeutralAnchor"),
            [-0.10855846107006073m, 0.020310375839471817m, -0.15915964543819427m, 0.9810559153556824m]);
        Assert.Contains("LeftMetacarpalSwingGain = 2.0\n", resource, StringComparison.Ordinal);
        Assert.Contains("RightMetacarpalSwingGain = 2.25\n", resource, StringComparison.Ordinal);
        Assert.Contains("MetacarpalHingeGateStart = 0.4\n", resource, StringComparison.Ordinal);
        Assert.Contains("MetacarpalHingeGateEnd = 0.625\n", resource, StringComparison.Ordinal);
        Assert.Contains("MetacarpalBendGateStart = 0.05\n", resource, StringComparison.Ordinal);
        Assert.Contains("MetacarpalBendGateEnd = 0.15\n", resource, StringComparison.Ordinal);
        Assert.Contains("no runtime fitting path", resource, StringComparison.Ordinal);
        Assert.Contains(
            "LeftMetacarpalSwingGain { get; set; } = 1.0f;",
            profileSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "RightMetacarpalSwingGain { get; set; } = 1.0f;",
            profileSource,
            StringComparison.Ordinal);

        var actualIdentities = new HashSet<(int Side, int Joint)>();
        foreach (string block in entryBlocks)
        {
            int side = ReadIntProperty(block, "Side", defaultValue: 0);
            int joint = ReadIntProperty(block, "Joint", defaultValue: 5);
            Assert.True(actualIdentities.Add((side, joint)), $"Duplicate calibration record {side}/{joint}.");
            Assert.True(
                _expectedSourceNeutrals.TryGetValue((side, joint), out decimal[]? expected),
                $"Unexpected calibration record {side}/{joint}.");

            decimal[] actual = ReadQuaternionProperty(block, "SourceNeutral");
            for (int component = 0; component < actual.Length; component++)
            {
                Assert.InRange(
                    decimal.Abs(actual[component] - expected![component]),
                    0m,
                    SerialisationTolerance);
            }

            decimal lengthSquared = actual.Sum(component => component * component);
            Assert.InRange(decimal.Abs(lengthSquared - 1m), 0m, 0.000000005m);

            if (_expectedThumbDestinationNeutrals.ContainsKey((side, joint)))
            {
                // Thumb records mirror the authored imported rest local rotations as provenance (XR-002 TR44,
                // TR27): metacarpal non-identity (≈95.26°), proximal/distal identity.
                decimal[] neutral = ReadQuaternionProperty(block, "DestinationNeutral");
                decimal[] expectedNeutral = _expectedThumbDestinationNeutrals[(side, joint)];
                for (int component = 0; component < neutral.Length; component++)
                {
                    Assert.InRange(
                        decimal.Abs(neutral[component] - expectedNeutral[component]),
                        0m,
                        SerialisationTolerance);
                }
            }
            else
            {
                Assert.DoesNotContain("DestinationNeutral =", block, StringComparison.Ordinal);
            }

            Assert.DoesNotContain("BasisCorrespondence =", block, StringComparison.Ordinal);
            Assert.DoesNotContain("Gain =", block, StringComparison.Ordinal);
            Assert.DoesNotContain("Clamp", block, StringComparison.OrdinalIgnoreCase);
        }

        Assert.True(actualIdentities.SetEquals(_expectedSourceNeutrals.Keys));
        Assert.Equal(6, _expectedThumbDestinationNeutrals.Count);
    }

    /// <summary>Guards that the player scene continues to bind the external profile without embedding it.</summary>
    [Fact]
    public void PlayerTemplateReferencesExternalCalibrationProfile()
    {
        string scene = File.ReadAllText(ResolveRepositoryPath(
            "game",
            "assets",
            "characters",
            "templates",
            "reference_female",
            "reference_female_player.tscn"));

        Assert.Contains(
            "[ext_resource type=\"Resource\" path=\"res://assets/xr/calibration/fingers_calibration_quest3.tres\" id=\"5_3ka35\"]",
            scene,
            StringComparison.Ordinal);
        Assert.Contains("CalibrationProfile = ExtResource(\"5_3ka35\")", scene, StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> ReadEntryBlocks(string resource)
    {
        string[] blocks = resource.Split("\n[sub_resource ", StringSplitOptions.None);
        return
        [
            .. blocks
            .Skip(1)
            .Select(block => block.Split("\n[", 2, StringSplitOptions.None)[0])
            .Where(block => block.Contains("script = ExtResource(\"1_bb707\")", StringComparison.Ordinal)),
        ];
    }

    private static int ReadIntProperty(string block, string propertyName, int defaultValue)
    {
        string prefix = $"{propertyName} = ";
        string? line = block.Split('\n').FirstOrDefault(candidate => candidate.StartsWith(prefix, StringComparison.Ordinal));
        return line is null
            ? defaultValue
            : int.Parse(line[prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    private static decimal[] ReadQuaternionProperty(string block, string propertyName)
    {
        string prefix = $"{propertyName} = Quaternion(";
        string line = block.Split('\n').Single(candidate => candidate.StartsWith(prefix, StringComparison.Ordinal));
        return
        [
            .. line[prefix.Length..^1]
                .Split(',', StringSplitOptions.TrimEntries)
                .Select(component => decimal.Parse(component, NumberStyles.Float, CultureInfo.InvariantCulture)),
        ];
    }

    private static void AssertDecimalQuaternion(decimal[] actual, decimal[] expected)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int component = 0; component < actual.Length; component++)
        {
            Assert.InRange(
                decimal.Abs(actual[component] - expected[component]),
                0m,
                SerialisationTolerance);
        }
    }

    private static string ResolveRepositoryPath(params string[] segments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate repository file '{Path.Combine(segments)}'.");
    }
}
