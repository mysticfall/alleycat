using AlleyCat.Core;
using AlleyCat.Vision;
using Godot;

namespace AlleyCat.Testing;

/// <summary>
/// Test-only visible target cue provider used to isolate AI-007 photobooth evidence from unrelated NPC composition.
/// </summary>
[GlobalClass]
public sealed partial class AttentionGazeTargetSelectionCueSubject : Node3D, IIdentifiable, IVisualSubject
{
    private static readonly IReadOnlyList<VisualCue> _emptyCues = [];

    /// <inheritdoc />
    [Export]
    public string Id { get; set; } = string.Empty;

    /// <inheritdoc />
    public string Type => "char";

    /// <inheritdoc />
    public string FullId => $"{Type}:{Id}";

    /// <inheritdoc />
    [Export]
    public VisualCue? Cue
    {
        get;
        set;
    }

    /// <inheritdoc />
    public IReadOnlyList<VisualCue> VisualCues { get; private set; } = _emptyCues;

    /// <inheritdoc />
    public override void _Ready()
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            throw new InvalidOperationException(
                $"AI-007 photobooth cue subject '{GetPath()}' requires a non-empty {nameof(Id)}.");
        }

        VisualCue cue = Cue
            ?? throw new InvalidOperationException(
                $"AI-007 photobooth cue subject '{GetPath()}' requires a {nameof(Cue)}.");
        VisualCues = [cue];
    }
}
