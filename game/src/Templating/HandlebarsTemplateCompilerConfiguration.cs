using Godot;
using GodotFileAccess = Godot.FileAccess;

namespace AlleyCat.Templating;

/// <summary>
/// Applies Godot-authored Handlebars compiler configuration to a plain compiler engine.
/// </summary>
internal static class HandlebarsTemplateCompilerConfiguration
{
    public static void Apply(
        HandlebarsTemplateCompilerEngine engine,
        string partialDirectoryPath,
        IEnumerable<ITemplateTool> tools,
        IEnumerable<TemplateToolResource> toolResources)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(toolResources);

        RegisterConfiguredPartials(engine, partialDirectoryPath);
        RegisterConfiguredTools(engine, tools, toolResources);
    }

    private static void RegisterConfiguredPartials(HandlebarsTemplateCompilerEngine engine, string partialDirectoryPath)
    {
        if (string.IsNullOrWhiteSpace(partialDirectoryPath))
        {
            return;
        }

        string[] filePaths = GetPartialFilePaths(partialDirectoryPath);
        foreach (string filePath in filePaths.Order(StringComparer.Ordinal))
        {
            string partialName = Path.GetFileNameWithoutExtension(filePath);
            engine.RegisterPartial(partialName, ReadAllText(filePath));
        }
    }

    private static void RegisterConfiguredTools(
        HandlebarsTemplateCompilerEngine engine,
        IEnumerable<ITemplateTool> tools,
        IEnumerable<TemplateToolResource> toolResources)
    {
        foreach (ITemplateTool tool in tools)
        {
            if (tool is null)
            {
                throw new InvalidOperationException("Configured template tools must not contain null entries.");
            }

            engine.RegisterTool(tool);
        }

        foreach (TemplateToolResource tool in toolResources)
        {
            if (tool is null)
            {
                throw new InvalidOperationException("Configured template tool resources must not contain null entries.");
            }

            engine.RegisterTool(tool);
        }
    }

    private static string[] GetPartialFilePaths(string directoryPath)
        => IsGodotPath(directoryPath) ? GetGodotPartialFilePaths(directoryPath) : GetFileSystemPartialFilePaths(directoryPath);

    private static string[] GetGodotPartialFilePaths(string directoryPath)
    {
        if (!DirAccess.DirExistsAbsolute(directoryPath))
        {
            throw new DirectoryNotFoundException($"Configured template partial directory '{directoryPath}' does not exist.");
        }

        using DirAccess directory = DirAccess.Open(directoryPath)
            ?? throw new InvalidOperationException(
                $"Configured template partial directory '{directoryPath}' could not be opened.");

        string[] fileNames = directory.GetFiles();
        string[] filePaths = new string[fileNames.Length];
        for (int index = 0; index < fileNames.Length; index++)
        {
            filePaths[index] = directoryPath.TrimEnd('/') + "/" + fileNames[index];
        }

        return filePaths;
    }

    private static string[] GetFileSystemPartialFilePaths(string directoryPath)
    {
        return Directory.Exists(directoryPath)
            ? Directory.GetFiles(directoryPath)
            : throw new DirectoryNotFoundException(
                $"Configured template partial directory '{directoryPath}' does not exist.");
    }

    private static string ReadAllText(string filePath)
    {
        if (!IsGodotPath(filePath))
        {
            return File.ReadAllText(filePath);
        }

        if (!GodotFileAccess.FileExists(filePath))
        {
            throw new FileNotFoundException($"Configured template partial file '{filePath}' does not exist.", filePath);
        }

        using GodotFileAccess file = GodotFileAccess.Open(filePath, GodotFileAccess.ModeFlags.Read)
            ?? throw new InvalidOperationException(
                $"Configured template partial file '{filePath}' could not be opened.");

        return file.GetAsText();
    }

    private static bool IsGodotPath(string path)
        => path.StartsWith("res://", StringComparison.Ordinal) || path.StartsWith("user://", StringComparison.Ordinal);
}
