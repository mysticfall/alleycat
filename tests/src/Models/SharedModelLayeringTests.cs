using System.Text.RegularExpressions;
using Xunit;

namespace AlleyCat.Tests.Models;

/// <summary>
/// CORE-011-TAC-001 (TR1–4): source review that the base layer has no ONNX Runtime, execution-provider, file-path or
/// Godot dependency.
/// </summary>
public sealed partial class SharedModelLayeringTests
{
    /// <summary>No base-layer source file references Godot, ONNX Runtime or file-system APIs.</summary>
    [Fact]
    public void BaseLayerSources_HaveNoGodotOnnxOrPathDependency()
    {
        string[] sources = Directory.GetFiles(RepositoryPath.Get("game", "src", "Models"), "*.cs");

        Assert.NotEmpty(sources);
        foreach (string path in sources)
        {
            string source = File.ReadAllText(path);
            Match forbidden = ForbiddenDependency().Match(source);
            Assert.False(forbidden.Success, $"{Path.GetFileName(path)} references '{forbidden.Value}'.");
        }
    }

    [GeneratedRegex(@"\busing\s+Godot\b|\bGodot\.|OnnxRuntime|InferenceSession|SessionOptions|ExecutionProvider|System\.IO|\bFile\.|\bPath\.|\bDirectory\.|res://")]
    private static partial Regex ForbiddenDependency();
}
