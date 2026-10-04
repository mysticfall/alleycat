using System.Text.RegularExpressions;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.AnimationCatalogue;

/// <summary>Guards migrated behavioural tests from silently returning to tracked runtime-content assertions.</summary>
public sealed class RuntimeFixtureIsolationIntegrationTests
{
    /// <inheritdoc/>
    [Fact]
    public void AutomatedCSharpTests_DoNotReferenceTrackedRuntimeContentOrAuthoredIdentifiers()
    {
        string repositoryRoot = Directory.GetParent(ProjectSettings.GlobalizePath("res://"))?.Parent?.FullName
            ?? throw new Xunit.Sdk.XunitException("Could not resolve the repository root.");
        string[] files = [
            .. Directory.EnumerateFiles(Path.Combine(repositoryRoot, "tests/src"), "*.cs", SearchOption.AllDirectories),
            .. Directory.EnumerateFiles(Path.Combine(repositoryRoot, "integration-tests/src"), "*.cs", SearchOption.AllDirectories),
        ];
        string[] forbidden = [
            // The real character-owned, variant-scoped runtime package root (ANIM-002 delivery contract TR4).
            "characters/reference/female/animations/runtime",
            "locomotion_sad",
            "loc_sad_",
            "\"sad\"",
            "locomotion.blend",
            "locomotion_template.blend",
            "kimodo_retarget_setup.blend",
        ];

        foreach (string file in files)
        {
            if (string.Equals(file, Path.Combine(repositoryRoot, "integration-tests/src/AnimationCatalogue/RuntimeFixtureIsolationIntegrationTests.cs"), StringComparison.Ordinal))
            {
                continue;
            }

            string source = File.ReadAllText(file);
            Assert.DoesNotContain(forbidden, term => source.Contains(term, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Guards the manual navigation evidence tools from automated execution paths.</summary>
    [Headless]
    [Fact]
    public void ManualNavigationTools_AreExcludedFromAutomatedPaths()
    {
        string repositoryRoot = Directory.GetParent(ProjectSettings.GlobalizePath("res://"))?.Parent?.FullName
            ?? throw new Xunit.Sdk.XunitException("Could not resolve the repository root.");
        string thisTestPath = Path.Combine(
            repositoryRoot,
            "integration-tests/src/AnimationCatalogue/RuntimeFixtureIsolationIntegrationTests.cs");
        string[] manualTools =
        [
            "navigation_playtest_photobooth",
        ];
        string[] automatedRoots =
        [
            "integration-tests/src",
            "tests/src",
            "game/src",
        ];

        foreach (string root in automatedRoots)
        {
            foreach (string sourcePath in Directory.EnumerateFiles(
                         Path.Combine(repositoryRoot, root),
                         "*.cs",
                         SearchOption.AllDirectories))
            {
                if (string.Equals(sourcePath, thisTestPath, StringComparison.Ordinal))
                {
                    continue;
                }

                string source = File.ReadAllText(sourcePath);
                Assert.DoesNotContain(
                    manualTools,
                    tool => source.Contains(tool, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    /// <summary>Prevents migrated runtime consumers from regaining authored inventory or exit-only publication oracles.</summary>
    [Fact]
    public void GeneralisedRuntimeSources_ContainNoAuthoredInventoryOrPublisherOracle()
    {
        string repositoryRoot = Directory.GetParent(ProjectSettings.GlobalizePath("res://"))?.Parent?.FullName
            ?? throw new Xunit.Sdk.XunitException("Could not resolve the repository root.");
        string[] migratedSources =
        [
            "game/src/Control/Locomotion/StandingLocomotionCatalogue.cs",
            "game/src/Control/Locomotion/PerFootPlantedIntervals.cs",
        ];
        // The delivered Reference Female package default paths (ANIM-002 delivery contract TR4) legitimately name the
        // variant directory; configuration defaults are not authored-inventory oracles, so they are excluded from the
        // variant-name scan while every other occurrence stays forbidden.
        string[] sanctionedDefaultPaths =
        [
            "res://assets/characters/reference/female/animations/runtime/locomotion/sad/locomotion_sad_catalogue.json",
        ];
        string[] forbiddenAuthoredTerms = ["sad", "loc_sad_", "f2i_l", "f2i_r"];
        string[] forbiddenRoleInventory = ["wf", "wb", "xl_l", "xr_l", "i2f_l", "i2b_l", "b2i_l"];

        foreach (string relativePath in migratedSources)
        {
            string source = File.ReadAllText(Path.Combine(repositoryRoot, relativePath));
            foreach (string sanctionedPath in sanctionedDefaultPaths)
            {
                source = source.Replace(sanctionedPath, string.Empty, StringComparison.Ordinal);
            }

            Assert.DoesNotContain(forbiddenAuthoredTerms, term => source.Contains(term, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotMatch(new Regex(@"\b22\b", RegexOptions.CultureInvariant), source);
            Assert.False(
                forbiddenRoleInventory.All(role => source.Contains($"\"{role}\"", StringComparison.Ordinal)),
                $"'{relativePath}' must not contain a fixed authored role inventory.");
        }
    }
}
