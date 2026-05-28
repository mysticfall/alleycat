namespace AlleyCat.Tests;

/// <summary>
/// Resolves source files from test binaries for source-level contract checks.
/// </summary>
internal static class RepositoryPath
{
    public static string Get(params string[] pathSegments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AlleyCat.sln")))
        {
            directory = directory.Parent;
        }

        return directory is not null
            ? Path.Combine([directory.FullName, .. pathSegments])
            : throw new InvalidOperationException("Could not locate the repository root from the test binary path.");
    }
}
