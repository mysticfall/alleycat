using System.Text.RegularExpressions;

namespace AlleyCat.IntegrationTests.Support.AI;

/// <summary>
/// Writes sanitised evidence-trace artefacts to a contributor-local directory that version control
/// excludes by default.
/// </summary>
/// <remarks>
/// <para>
/// Raw trace artefacts are never committed: the default root is <c>game/temp/live-traces</c>, inside the
/// <c>game/temp</c> scratch area that <c>.gitignore</c> already excludes as the designated location for persisted
/// temporary files, so no dedicated ignore entry is needed. Inject a different root only for deterministic tests or
/// diagnostics. Artefacts are stored separately from evaluation results, so raw evidence survives judge outcomes.
/// </para>
/// </remarks>
public sealed class LiveLLMTraceWriter(DirectoryInfo? root = null)
{
    /// <summary>Name of the contributor-local directory holding raw trace artefacts.</summary>
    public const string RootDirectoryName = "live-traces";

    private static readonly Regex _unsafeFileNameCharacters = new(@"[^a-zA-Z0-9_.-]+", RegexOptions.Compiled);

    /// <summary>Directory artefacts are written to.</summary>
    public DirectoryInfo Root { get; } = root ?? ResolveDefaultRoot();

    /// <summary>
    /// Writes one trace artefact and returns its file.
    /// </summary>
    /// <param name="trace">The trace to persist.</param>
    /// <param name="label">Optional label folded into the file name, for example the trial index.</param>
    /// <returns>The written artefact file.</returns>
    public FileInfo Write(LiveLLMTrace trace, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(trace);

        Root.Create();
        string scenarioName = SafeFileName(trace.ScenarioName);
        string suffix = string.IsNullOrWhiteSpace(label) ? string.Empty : $"-{SafeFileName(label)}";
        string fileName =
            $"{scenarioName}{suffix}-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffffffZ}-{Guid.NewGuid().ToString("N")[..8]}.json";
        string path = Path.Combine(Root.FullName, fileName);
        File.WriteAllText(path, trace.ToJsonString());
        return new FileInfo(path);
    }

    /// <summary>
    /// Resolves the default contributor-local trace directory by walking up from this assembly's location
    /// to the integration-test project directory, then resolving <c>game/temp/live-traces</c> relative to the
    /// repository root (the project directory's parent). The assembly location is the anchor because tests run
    /// inside a Godot host process whose own base directory belongs to the game.
    /// </summary>
    /// <returns>The default trace artefact directory.</returns>
    public static DirectoryInfo ResolveDefaultRoot()
    {
        string assemblyLocation = typeof(LiveLLMTraceWriter).Assembly.Location;
        DirectoryInfo? current = string.IsNullOrEmpty(assemblyLocation)
            ? new(AppContext.BaseDirectory)
            : new(Path.GetDirectoryName(assemblyLocation)!);
        while (current is not null)
        {
            if (current.Name == "integration-tests"
                && File.Exists(Path.Combine(current.FullName, "AlleyCat.IntegrationTests.csproj"))
                && current.Parent is { } repositoryRoot)
            {
                return new DirectoryInfo(Path.Combine(repositoryRoot.FullName, "game", "temp", RootDirectoryName));
            }

            current = current.Parent;
        }

        return new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, RootDirectoryName));
    }

    private static string SafeFileName(string value)
        => _unsafeFileNameCharacters.Replace(value.Trim(), "_").Trim('.') is { Length: > 0 } safe ? safe : "scenario";
}
