using AlleyCat.Mind.Observation;
using Godot;
using MindBase = AlleyCat.Mind.Mind;

namespace AlleyCat.Testing;

/// <summary>
/// Test-only Mind for the AI-009 orienting photobooth that bypasses the production agentic lifecycle while keeping
/// the template-authored Mind children (AI-007 selector and the orienting controller) intact.
/// </summary>
/// <remarks>
/// The photobooth simulates the AI-007 gaze-selector role through <see cref="Ai009OrientingPhotoboothDriver"/>
/// gaze assignment, so this Mind performs no observation processing of its own.
/// </remarks>
[GlobalClass]
public sealed partial class Ai009OrientingPhotoboothMind : MindBase
{
    /// <inheritdoc />
    public override void _EnterTree()
    {
    }

    /// <inheritdoc />
    public override void _Ready()
    {
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
    }

    /// <inheritdoc />
    protected override Task ProcessObservationsAsync(
        IReadOnlyList<Observation> observations,
        IReadOnlyList<Observation> timelineSnapshot,
        CancellationToken cancellationToken)
        => Task.CompletedTask;
}
