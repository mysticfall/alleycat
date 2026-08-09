using Xunit;

namespace AlleyCat.Tests.Architecture;

/// <summary>Guards the approved Body/Character-to-Mind dependency boundary without assembly partitioning.</summary>
public sealed class PerceptSensingDependencyTests
{
    /// <summary>Sense stays body/mind-free while Body and Character stay mind-free.</summary>
    [Fact]
    public void ProductionSense_DoesNotReferenceBodyOrMind_AndBodyCharacterDoNotReferenceMind()
    {
        AssertDirectoryDoesNotReference("game", "src", "Sense", forbiddenNamespace: "AlleyCat.Body");
        AssertDirectoryDoesNotReference("game", "src", "Sense", forbiddenNamespace: "AlleyCat.Mind");
        AssertDirectoryDoesNotReference("game", "src", "Body", forbiddenNamespace: "AlleyCat.Mind");
        AssertDirectoryDoesNotReference("game", "src", "Character", forbiddenNamespace: "AlleyCat.Mind");
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
