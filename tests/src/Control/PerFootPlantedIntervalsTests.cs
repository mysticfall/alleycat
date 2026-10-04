using System.Text;
using System.Text.Json.Nodes;
using AlleyCat.Control.Locomotion;
using Xunit;

#pragma warning disable CS1591

namespace AlleyCat.Tests.Control;

/// <summary>
/// Pure schema and lookup tests for the ANIM-002 per-foot planted-interval reader, using a synthetic document with
/// one looping and one finite clip. No engine resource access is involved.
/// </summary>
public sealed class PerFootPlantedIntervalsTests
{
    private const string ValidJson = /*lang=json,strict*/ """
        {
          "schema_version": 1,
          "contract": "anim002_per_foot_planted_intervals_v1",
          "character": "unit_character",
          "package": "unit_package",
          "variant": "unit_variant",
          "derivation": {
            "tool": "derive_planted_intervals",
            "tool_version": 3,
            "policy": { "identity": "unit_policy", "sha256": "unit" },
            "runtime_library_sha256": "1d19ab6cea1454c45620dc7b4d5e92b28aab1b9103ed9e83de82d05fb918395b"
          },
          "clips": [
            {
              "identity": "unit_cycle",
              "role": "unit_cycle_role",
              "capability": "cyclic_forward_primary",
              "looping": true,
              "duration_seconds": 1.0,
              "feet": {
                "left": {
                  "baseline_ball_height_m": 0.01,
                  "segments": [
                    { "state": "planted", "start_seconds": 0.0, "end_seconds": 0.4 },
                    { "state": "swing", "start_seconds": 0.4, "end_seconds": 0.9 },
                    { "state": "planted", "start_seconds": 0.9, "end_seconds": 1.0 }
                  ]
                },
                "right": {
                  "baseline_ball_height_m": 0.01,
                  "segments": [
                    { "state": "swing", "start_seconds": 0.0, "end_seconds": 0.5 },
                    { "state": "planted", "start_seconds": 0.5, "end_seconds": 1.0 }
                  ]
                }
              }
            },
            {
              "identity": "unit_exit",
              "role": "unit_exit_role",
              "capability": "exit_forward_left",
              "looping": false,
              "duration_seconds": 2.0,
              "feet": {
                "left": {
                  "baseline_ball_height_m": 0.02,
                  "segments": [
                    { "state": "swing", "start_seconds": 0.0, "end_seconds": 0.6 },
                    { "state": "planted", "start_seconds": 0.6, "end_seconds": 2.0 }
                  ]
                },
                "right": {
                  "baseline_ball_height_m": 0.02,
                  "segments": [ { "state": "planted", "start_seconds": 0.0, "end_seconds": 2.0 } ]
                }
              }
            }
          ]
        }
        """;

    [Fact]
    public void Parse_ExposesPerClipPerFootSegmentsByIdentity()
    {
        var intervals = PerFootPlantedIntervals.Parse(ValidJson);

        Assert.Equal("unit_package", intervals.Package);
        Assert.Equal("unit_variant", intervals.Variant);
        Assert.Equal("unit_character", intervals.Character);
        Assert.Equal(2, intervals.Clips.Count);
        PlantedClipIntervals cycle = intervals["unit_cycle"];
        Assert.True(cycle.IsLooping);
        Assert.Equal("cyclic_forward_primary", cycle.Capability);
        Assert.Equal(
            [new PlantedIntervalSegment(0d, 0.4d, true), new(0.4d, 0.9d, false), new(0.9d, 1d, true)],
            (IEnumerable<PlantedIntervalSegment>)cycle.Left);
        Assert.Equal(cycle.Right, cycle.SegmentsFor(StandingLocomotionAnatomicalSide.Right));
        Assert.False(intervals.TryGetClip("unit_missing", out _));
        _ = Assert.Throws<KeyNotFoundException>(() => intervals["unit_missing"]);
    }

    [Theory]
    [InlineData(0.0, true)]
    [InlineData(0.39, true)]
    [InlineData(0.4, false)]
    [InlineData(0.89, false)]
    [InlineData(0.9, true)]
    [InlineData(1.0, true)]
    [InlineData(1.5, false)]
    [InlineData(-0.05, true)]
    [InlineData(-0.5, false)]
    public void IsPlanted_LoopingClipUsesHalfOpenSegmentsAndWrapsSourceTime(double time, bool planted) =>
        Assert.Equal(planted, PerFootPlantedIntervals.Parse(ValidJson).IsPlanted("unit_cycle", StandingLocomotionAnatomicalSide.Left, time));

    [Theory]
    [InlineData(-1.0, false)]
    [InlineData(0.59, false)]
    [InlineData(0.6, true)]
    [InlineData(2.0, true)]
    [InlineData(5.0, true)]
    public void IsPlanted_FiniteClipClampsSourceTime(double time, bool planted)
    {
        PlantedClipIntervals exit = PerFootPlantedIntervals.Parse(ValidJson)["unit_exit"];

        Assert.Equal(planted, exit.IsPlanted(StandingLocomotionAnatomicalSide.Left, time));
        Assert.True(exit.IsPlanted(StandingLocomotionAnatomicalSide.Right, time));
    }

    [Fact]
    public void SegmentAt_ReturnsTheContainingSegmentIncludingTheClipEnd()
    {
        PlantedClipIntervals exit = PerFootPlantedIntervals.Parse(ValidJson)["unit_exit"];

        Assert.Equal(new PlantedIntervalSegment(0d, 0.6d, false), exit.SegmentAt(StandingLocomotionAnatomicalSide.Left, 0.3d));
        Assert.Equal(new PlantedIntervalSegment(0.6d, 2d, true), exit.SegmentAt(StandingLocomotionAnatomicalSide.Left, 2d));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => exit.SegmentAt(StandingLocomotionAnatomicalSide.Left, double.NaN));
    }

    [Fact]
    public void ParseIdleCatalogue_ReadsTheEmbeddedField()
    {
        var catalogue = new JsonObject
        {
            ["schema_version"] = 4,
            ["package"] = "idle",
            [PerFootPlantedIntervals.CatalogueField] = JsonNode.Parse(ValidJson),
        };

        var intervals = PerFootPlantedIntervals.ParseIdleCatalogue(catalogue.ToJsonString());

        Assert.True(intervals.IsPlanted("unit_exit", StandingLocomotionAnatomicalSide.Right, 1d));
        _ = catalogue.Remove(PerFootPlantedIntervals.CatalogueField);
        _ = Assert.Throws<InvalidDataException>(() => PerFootPlantedIntervals.ParseIdleCatalogue(catalogue.ToJsonString()));
    }

    [Fact]
    public void Parse_AcceptsIntegralVersionsWrittenAsFloatingPoint()
    {
        JsonObject document = Document();
        document["schema_version"] = 1.0;
        document["derivation"]!["tool_version"] = 3.0;

        Assert.Equal(2, PerFootPlantedIntervals.Parse(document.ToJsonString()).Clips.Count);
    }

    public static TheoryData<string, Action<JsonObject>> Mutations => new()
    {
        { "unknown schema version", document => document["schema_version"] = 2 },
        { "fractional schema version", document => document["schema_version"] = 1.5 },
        { "unknown contract", document => document["contract"] = "legacy_contract" },
        { "unknown field", document => document["legacy_track"] = true },
        { "unknown tool", document => document["derivation"]!["tool"] = "hand_authored" },
        { "unknown tool version", document => document["derivation"]!["tool_version"] = 2 },
        { "missing library digest", document => ((JsonObject)document["derivation"]!).Remove("runtime_library_sha256") },
        { "malformed library digest", document => document["derivation"]!["runtime_library_sha256"] = "unit" },
        { "missing derivation", document => document.Remove("derivation") },
        { "no clips", document => document["clips"] = new JsonArray() },
        { "duplicate clip", document => ((JsonArray)document["clips"]!).Add(Clip(document, 0).DeepClone()) },
        { "missing foot", document => ((JsonObject)Clip(document, 0)["feet"]!).Remove("right") },
        { "gap", document => Segment(document, 0, "left", 1)["start_seconds"] = 0.45 },
        { "short coverage", document => Segment(document, 0, "left", 2)["end_seconds"] = 0.95 },
        { "repeated state", document => Segment(document, 0, "left", 1)["state"] = "planted" },
        { "unknown state", document => Segment(document, 0, "left", 1)["state"] = "hovering" },
        { "empty segment", document => Segment(document, 1, "right", 0)["end_seconds"] = 0.0 },
        { "non-boolean looping", document => Clip(document, 0)["looping"] = "yes" },
        { "non-positive duration", document => Clip(document, 1)["duration_seconds"] = 0.0 },
    };

    [Theory]
    [MemberData(nameof(Mutations))]
    public void Parse_RejectsInvalidDocuments(string name, Action<JsonObject> mutate)
    {
        JsonObject document = Document();
        mutate(document);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => PerFootPlantedIntervals.Parse(document.ToJsonString()));
        Assert.False(string.IsNullOrWhiteSpace(error.Message), name);
    }

    // Raw-text mutations: JSON objects cannot hold a duplicated key, so these edit the document text.
    public static TheoryData<string, string, string> DuplicatedOrMissingKeys => new()
    {
        { "duplicated root key replacing another", "\"variant\": \"unit_variant\"", "\"package\": \"unit_package\"" },
        { "duplicated derivation key", "\"tool\": \"derive_planted_intervals\",", "\"tool\": \"derive_planted_intervals\", \"tool\": \"derive_planted_intervals\"," },
        { "duplicated segment key replacing another", "\"state\": \"planted\", \"start_seconds\": 0.0, \"end_seconds\": 0.4", "\"state\": \"planted\", \"start_seconds\": 0.0, \"start_seconds\": 0.4" },
        { "missing derivation tool", "\"tool\": \"derive_planted_intervals\",", string.Empty },
    };

    [Theory]
    [MemberData(nameof(DuplicatedOrMissingKeys))]
    public void Parse_RejectsDuplicatedOrMissingKeysAsInvalidData(string name, string original, string replacement)
    {
        Assert.Contains(original, ValidJson, StringComparison.Ordinal);
        string json = ValidJson.Replace(original, replacement, StringComparison.Ordinal);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => PerFootPlantedIntervals.Parse(json));
        Assert.False(string.IsNullOrWhiteSpace(error.Message), name);
    }

    [Fact]
    public void ValidateAgainst_BindsCharacterPackageVariantAndEveryClipField()
    {
        var intervals = PerFootPlantedIntervals.Parse(ValidJson);
        PlantedClipExpectation[] clips =
        [
            new("unit_cycle", "unit_cycle_role", "cyclic_forward_primary", true, 1d),
            new("unit_exit", "unit_exit_role", "exit_forward_left", false, 2d),
        ];

        intervals.ValidateAgainst("unit_character", "unit_package", "unit_variant", clips);
        _ = Assert.Throws<InvalidDataException>(() => intervals.ValidateAgainst("other_character", "unit_package", "unit_variant", clips));
        _ = Assert.Throws<InvalidDataException>(() => intervals.ValidateAgainst("unit_character", "other_package", "unit_variant", clips));
        _ = Assert.Throws<InvalidDataException>(() => intervals.ValidateAgainst("unit_character", "unit_package", "other_variant", clips));
        _ = Assert.Throws<InvalidDataException>(() => intervals.ValidateAgainst("unit_character", "unit_package", "unit_variant", clips[..1]));
        PlantedClipExpectation[][] mismatches =
        [
            [clips[0] with { Capability = "cyclic_backward_primary" }, clips[1]],
            [clips[0] with { Role = "other_role" }, clips[1]],
            [clips[0] with { IsLooping = false }, clips[1]],
            [clips[0] with { DurationSeconds = 1.1d }, clips[1]],
            [clips[0] with { Identity = "unit_other" }, clips[1]],
        ];
        foreach (PlantedClipExpectation[] mismatch in mismatches)
        {
            _ = Assert.Throws<InvalidDataException>(() => intervals.ValidateAgainst("unit_character", "unit_package", "unit_variant", mismatch));
        }
    }

    [Fact]
    public void ValidateLibraryBinding_HashesTheLibraryWithoutItsPathOwnedUID()
    {
        var intervals = PerFootPlantedIntervals.Parse(ValidJson);
        const string Library = "[gd_resource type=\"AnimationLibrary\" format=3]\n\n[resource]\n";

        intervals.ValidateLibraryBinding(Encoding.UTF8.GetBytes(Library));
        intervals.ValidateLibraryBinding(Encoding.UTF8.GetBytes(
            "[gd_resource type=\"AnimationLibrary\" format=3 uid=\"uid://b1unit\"]\n\n[resource]\n"));
        _ = Assert.Throws<InvalidDataException>(() => intervals.ValidateLibraryBinding(
            Encoding.UTF8.GetBytes(Library.Replace("[resource]", "[resource]\nlength = 1.0", StringComparison.Ordinal))));
        _ = Assert.Throws<InvalidDataException>(() => intervals.ValidateLibraryBinding(Encoding.UTF8.GetBytes(
            "[gd_resource type=\"AnimationLibrary\" format=3]\n\n[resource]\n; uid=\"uid://b1unit\"\n")));
        Assert.Equal(intervals.RuntimeLibrarySha256, PerFootPlantedIntervals.ComputeRuntimeLibrarySha256(Encoding.UTF8.GetBytes(Library)));
    }

    public static TheoryData<string, string, string?> CanonicalisationVectors()
    {
        string path = RepositoryPath.Get("tools", "kimodo-pipeline", "tests", "fixtures", "runtime_library_canonicalisation_vectors.json");
        JsonNode document = JsonNode.Parse(File.ReadAllText(path))!;
        TheoryData<string, string, string?> data = [];
        foreach (JsonNode? vector in document["vectors"]!.AsArray())
        {
            data.Add(
                vector!["name"]!.GetValue<string>(),
                vector["content_hex"]!.GetValue<string>(),
                vector["canonical_hex"]?.GetValue<string>());
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CanonicalisationVectors))]
    public void CanonicalRuntimeLibraryBytes_MatchesTheSharedPipelineParityVectors(string name, string contentHex, string? canonicalHex)
    {
        byte[] content = Convert.FromHexString(contentHex);
        byte[] expected = canonicalHex is null ? content : Convert.FromHexString(canonicalHex);

        Assert.True(expected.AsSpan().SequenceEqual(PerFootPlantedIntervals.CanonicalRuntimeLibraryBytes(content)), name);
    }

    [Theory]
    [InlineData("res://assets/characters/unit_owner/unit_character/animations/runtime/unit_set/unit_variant/unit_set_unit_variant.tres", "unit_character")]
    [InlineData("res://assets/characters/other/male/animations/runtime/idle/calm/idle_calm_catalogue.json", "male")]
    public void CharacterOf_ReadsTheCharacterDirectoryAboveTheRuntimePackage(string path, string character) =>
        Assert.Equal(character, PerFootPlantedIntervals.CharacterOf(path));

    [Fact]
    public void CharacterOf_RejectsAPathOutsideACharacterRuntimePackage() =>
        _ = Assert.Throws<InvalidDataException>(() => PerFootPlantedIntervals.CharacterOf("res://temp/unit_set_unit_variant.tres"));

    [Fact]
    public void ParseValidatedIdleCatalogue_BindsTheEmbeddedIntervalsToTheirCatalogue()
    {
        JsonObject catalogue = IdleCatalogue();

        var intervals = PerFootPlantedIntervals.ParseValidatedIdleCatalogue(catalogue.ToJsonString(), "unit_character", out string library);

        Assert.Equal("unit_library.tres", library);
        Assert.Equal(2, intervals.Clips.Count);
        _ = Assert.Throws<InvalidDataException>(() => PerFootPlantedIntervals.ParseValidatedIdleCatalogue(catalogue.ToJsonString(), "other_character", out _));
        Action<JsonObject>[] mutations =
        [
            value => value["package_variant"] = "other_variant",
            value => value["clips"]![0]!["capability"] = "idle_other",
            value => value["clips"]![1]!["loop_intent"] = true,
            value => ((JsonArray)value["clips"]!).RemoveAt(1),
            value => value["animation_library"] = "../unit_library.tres",
            value => value.Remove(PerFootPlantedIntervals.CatalogueField),
        ];
        foreach (Action<JsonObject> mutate in mutations)
        {
            JsonObject copy = IdleCatalogue();
            mutate(copy);
            _ = Assert.Throws<InvalidDataException>(() => PerFootPlantedIntervals.ParseValidatedIdleCatalogue(copy.ToJsonString(), "unit_character", out _));
        }
    }

    [Fact]
    public void CapabilityIdentifier_RoundTripsEveryCapability()
    {
        foreach (StandingLocomotionCapability capability in Enum.GetValues<StandingLocomotionCapability>())
        {
            Assert.Equal(capability, StandingLocomotionCatalogue.ParseCapability(StandingLocomotionCatalogue.CapabilityIdentifier(capability)));
        }

        Assert.Equal("cyclic_lateral_left_right_lane", StandingLocomotionCatalogue.CapabilityIdentifier(StandingLocomotionCapability.CyclicLateralLeftRightLane));
    }

    [Fact]
    public void IsPlanted_LookupDoesNotAllocate()
    {
        var intervals = PerFootPlantedIntervals.Parse(ValidJson);
        bool planted = false;
        for (int index = 0; index < 64; index++)
        {
            planted ^= intervals.IsPlanted("unit_cycle", StandingLocomotionAnatomicalSide.Left, index * 0.05d);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 10_000; index++)
        {
            planted ^= intervals.IsPlanted("unit_cycle", StandingLocomotionAnatomicalSide.Left, index * 0.013d);
            planted ^= intervals.IsPlanted("unit_exit", StandingLocomotionAnatomicalSide.Right, index * 0.007d);
            planted ^= intervals.TryGetClip("unit_exit", out PlantedClipIntervals exit)
                && exit.SegmentAt(StandingLocomotionAnatomicalSide.Left, index * 0.003d).IsPlanted;
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0L, allocated);
        GC.KeepAlive(planted);
    }

    private static JsonObject IdleCatalogue() => new()
    {
        ["schema_version"] = 4,
        ["package"] = "unit_package",
        ["package_variant"] = "unit_variant",
        ["animation_library"] = "unit_library.tres",
        ["clip_count"] = 2,
        ["clips"] = new JsonArray(
            new JsonObject
            {
                ["identity"] = "unit_cycle",
                ["role"] = "unit_cycle_role",
                ["capability"] = "cyclic_forward_primary",
                ["loop_intent"] = true,
                ["duration_seconds"] = 1.0,
                ["boundary_kind"] = "cyclic_travel",
            },
            new JsonObject
            {
                ["identity"] = "unit_exit",
                ["role"] = "unit_exit_role",
                ["capability"] = "exit_forward_left",
                ["loop_intent"] = false,
                ["duration_seconds"] = 2.0,
            }),
        [PerFootPlantedIntervals.CatalogueField] = JsonNode.Parse(ValidJson),
    };

    private static JsonObject Document() => (JsonObject)JsonNode.Parse(ValidJson)!;

    private static JsonObject Clip(JsonObject document, int index) => (JsonObject)document["clips"]![index]!;

    private static JsonObject Segment(JsonObject document, int clip, string foot, int index) =>
        (JsonObject)Clip(document, clip)["feet"]![foot]!["segments"]![index]!;
}
