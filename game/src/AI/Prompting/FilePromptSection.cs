using Godot;

namespace AlleyCat.AI.Prompting;

/// <summary>
/// Prompt section that loads its content from a Godot resource path.
/// </summary>
[GlobalClass]
public partial class FilePromptSection : PromptSection
{
    /// <summary>
    /// Godot resource path to a text prompt file, for example <c>res://prompts/my_prompt.md</c>.
    /// </summary>
    [Export(PropertyHint.File, "*.md,*.txt")]
    public string FilePath { get; set; } = string.Empty;

    /// <inheritdoc />
    public override string GetContent()
    {
        if (string.IsNullOrWhiteSpace(FilePath))
        {
            throw new InvalidOperationException("File prompt section requires a non-empty Godot resource path.");
        }

        using var file = Godot.FileAccess.Open(FilePath, Godot.FileAccess.ModeFlags.Read);
        if (file is null)
        {
            Error error = Godot.FileAccess.GetOpenError();
            throw new InvalidOperationException(
                $"Could not read prompt file '{FilePath}'. Godot FileAccess error: {error}.");
        }

        return file.GetAsText();
    }
}
