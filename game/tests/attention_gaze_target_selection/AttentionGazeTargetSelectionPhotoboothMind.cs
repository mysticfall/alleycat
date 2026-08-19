using AlleyCat.Character;
using AlleyCat.Common;
using AlleyCat.Context;
using AlleyCat.Core;
using AlleyCat.Mind.Attention;
using AlleyCat.Scene;
using AlleyCat.Vision;
using Godot;
using MindBase = AlleyCat.Mind.Mind;

namespace AlleyCat.Testing;

/// <summary>
/// Test-only Mind that exposes controlled attention snapshots while retaining the production Mind and selector path.
/// </summary>
[GlobalClass]
public sealed partial class AttentionGazeTargetSelectionPhotoboothMind : MindBase
{
    private double _clockSeconds;

    /// <inheritdoc />
    public override void _EnterTree()
    {
        AttentionGazeTargetSelector selector = this.RequireNode<AttentionGazeTargetSelector>("AttentionGazeTargetSelector");
        selector.EvaluationIntervalSeconds = 0.01f;
        selector.PrimaryDwellSeconds = 10f;
        selector.SecondaryDwellSeconds = 0.5f;
        selector.SecondaryGlanceProbability = 0f;
        selector.SetSceneContextLoaderForTesting(CreateSceneContext);
        selector.SetRandomForTesting(FixedRandom.Instance);
    }

    /// <inheritdoc />
    public override void _Ready() => SetAttentionClockForTesting(() => _clockSeconds);

    /// <inheritdoc />
    public override void _ExitTree()
    {
    }

    /// <summary>Atomically replaces test attention values before the selector's normal next evaluation boundary.</summary>
    public void SetAttentionWeights(
        string dominantSubjectFullId,
        float dominantAttention)
    {
        _clockSeconds += 10d;
        AttentionDecayPerSecond = 1f;
        _ = GetAttentionSnapshot();
        AttentionDecayPerSecond = 0f;

        var settings = AttentionSettings.Create(
            maximum: 1f,
            decayPerSecond: 0f,
            retentionThreshold: 0.01f,
            contextThreshold: 0.01f);
        ReinforceAttention(dominantSubjectFullId, dominantAttention, settings);
    }

    private ISceneContext CreateSceneContext()
    {
        SceneTree tree = GetTree() ?? throw new InvalidOperationException("AI-007 photobooth Mind requires a SceneTree.");
        var characters = new List<ICharacter>();
        foreach (Node node in tree.GetNodesInGroup("Actors"))
        {
            if (node is ICharacter character
                && !string.IsNullOrWhiteSpace(character.Id)
                && character.Id == "observer")
            {
                characters.Add(character);
            }
        }

        foreach (Node node in tree.GetNodesInGroup("AI007CueSubjects"))
        {
            if (node is not AttentionGazeTargetSelectionCueSubject cueSubject)
            {
                throw new InvalidOperationException(
                    $"AI-007 photobooth cue-subject group member '{node.GetPath()}' has unexpected type '{node.GetType().FullName}'.");
            }

            characters.Add(new CueSubjectCharacter(cueSubject));
        }

        return new SceneContext(characters);
    }

    private sealed class CueSubjectCharacter(AttentionGazeTargetSelectionCueSubject subject) : ICharacter
    {
        public string Id
        {
            get => subject.Id;
            set => throw new InvalidOperationException("AI-007 photobooth cue subjects have authored identities.");
        }

        public IReadOnlyList<IComponent> Components { get; } = [];

        public IReadOnlyList<VisualCue> VisualCues => subject.VisualCues;

        public IReadOnlyDictionary<string, object?> GetContext(ISceneContext scene, IContextual? observer)
            => new Dictionary<string, object?>();
    }

    private sealed class FixedRandom : IAttentionGazeRandom
    {
        public static FixedRandom Instance
        {
            get;
        } = new();

        public double NextUnitInterval() => 0d;
    }
}
