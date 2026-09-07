using AlleyCat.Control.Locomotion;
using Xunit;

#pragma warning disable CS1591

namespace AlleyCat.Tests.Control;

/// <summary>
/// Pure schema tests for the embedded-only runtime catalogue contract. Every case fails before any engine resource
/// access, so the JSON boundary (schema version gate, co-located library filename, absence of per-clip path fields)
/// is validated without a running Godot runtime.
/// </summary>
public sealed class StandingLocomotionCatalogueSchemaTests
{
    private const string CataloguePath = "res://unit-fixtures/locomotion/fixture_catalogue.json";

    private const string ValidV3Json = /*lang=json,strict*/ """
        {
          "schema_version": 3,
          "package": "unit_fixture",
          "package_variant": "fixture",
          "animation_library": "unit_library.tres",
          "clip_count": 1,
          "clips": [
            {
              "identity": "unit_token_a",
              "role": "declared_stride",
              "capability": "cyclic_forward_primary",
              "boundary_kind": "cyclic_travel",
              "loop_intent": true,
              "duration_seconds": 1.0,
              "phase_events": {
                "applicability": "applicable",
                "contract": "anim002_post_repair_directional_phase_events_v2",
                "evaluation_stage": "final_repaired_evaluated_hierarchy",
                "duration_seconds": 1.0,
                "timeline": { "fps": 100, "frame_start": 0, "frame_end": 100 },
                "sample_semantics": {
                  "conceptual_sample_count": 101,
                  "duplicate_endpoint": true,
                  "is_loop": true,
                  "unique_runtime_sample_count": 100
                },
                "events": [
                  {
                    "accepted": true,
                    "anatomical_side": "Left",
                    "boundary_context": null,
                    "canonicalised_from_duplicate_endpoint": null,
                    "confidence": 1.0,
                    "derivation_event_id": "release_l",
                    "derived_output_frame": 20,
                    "event_kind": "Release",
                    "id": "release_l",
                    "measurement": {},
                    "output_frame": 20,
                    "reason": "unit fixture event",
                    "repair_frame_delta": 0,
                    "seed_displacement_provenance": {},
                    "time_seconds": 0.2
                  }
                ]
              },
              "stitch_mappings": { "applicability": "applicable", "mappings": [] },
              "endpoint_root_displacement_godot": [0.0, 0.0, -1.0],
              "endpoint_local_y_yaw": 0.0
            }
          ]
        }
        """;

    [Fact]
    public void Parse_RejectsStaleV2ClipPathCataloguesWithClearDiagnostic()
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => StandingLocomotionCatalogue.Parse(ValidV3Json.Replace("\"schema_version\": 3", "\"schema_version\": 2"), CataloguePath));

        Assert.Contains("stale schema version 2", error.Message, StringComparison.Ordinal);
        Assert.Contains("schema version 3", error.Message, StringComparison.Ordinal);
        Assert.Contains(CataloguePath, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"schema_version\": 1")]
    [InlineData("\"schema_version\": 4")]
    public void Parse_RejectsUnsupportedSchemaVersions(string versionFragment)
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => StandingLocomotionCatalogue.Parse(ValidV3Json.Replace("\"schema_version\": 3", versionFragment), CataloguePath));

        Assert.Contains("schema version 3", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"animation_library\": \"res://elsewhere/unit_library.tres\"")]
    [InlineData("\"animation_library\": \"nested/unit_library.tres\"")]
    [InlineData("\"animation_library\": \"../unit_library.tres\"")]
    [InlineData("\"animation_library\": \"unit_library.res\"")]
    public void Parse_RejectsNonFilenameAnimationLibraryDeclarations(string libraryFragment)
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => StandingLocomotionCatalogue.Parse(
                ValidV3Json.Replace("\"animation_library\": \"unit_library.tres\"", libraryFragment),
                CataloguePath));

        Assert.Contains("co-located", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsPerClipResourcePathFields()
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => StandingLocomotionCatalogue.Parse(
                ValidV3Json.Replace("\"identity\": \"unit_token_a\"", "\"clip\": \"res://clips/unit_token_a.res\", \"identity\": \"unit_token_a\""),
                CataloguePath));

        Assert.Contains("unknown or duplicate property", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsCataloguePathsWithoutDirectory()
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => StandingLocomotionCatalogue.Parse(ValidV3Json, "bare_catalogue.json"));

        Assert.Contains("directory-qualified", error.Message, StringComparison.Ordinal);
    }
}
