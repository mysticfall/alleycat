using Xunit;

namespace AlleyCat.Tests.Architecture;

/// <summary>Guards the approved modality, Sense, Character, and Mind dependency boundary without assembly partitioning.</summary>
public sealed class PerceptSensingDependencyTests
{
    /// <summary>Modality implementations, Sense, and Character stay Mind-free while Sense remains delivery-domain-neutral.</summary>
    [Fact]
    public void ProductionModalitiesSenseAndCharacter_DoNotReferenceMind_AndSenseDoesNotReferenceRigging()
    {
        AssertDirectoryDoesNotReference("game", "src", "Sense", forbiddenNamespace: "AlleyCat.Rigging");
        AssertDirectoryDoesNotReference("game", "src", "Sense", forbiddenNamespace: "AlleyCat.Mind");
        AssertDirectoryDoesNotReference("game", "src", "Character", forbiddenNamespace: "AlleyCat.Mind");
        AssertDirectoryDoesNotReference("game", "src", "Speech", forbiddenNamespace: "AlleyCat.Mind");
        AssertDirectoryDoesNotReference("game", "src", "Vision", forbiddenNamespace: "AlleyCat.Mind");
        AssertDirectoryDoesNotReference("game", "src", "Vision", forbiddenNamespace: "AlleyCat.Speech.Voice");
    }

    private static void AssertDirectoryDoesNotReference(string first, string second, string third, string forbiddenNamespace)
    {
        string directory = RepositoryPath.Get(first, second, third);
        foreach (string sourceFile in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
        {
            string source = File.ReadAllText(sourceFile);
            Assert.DoesNotContain(forbiddenNamespace, source, StringComparison.Ordinal);
        }
    }
}
