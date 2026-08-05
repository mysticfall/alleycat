using AlleyCat.Body.Eyes;
using AlleyCat.Body.Hands;
using AlleyCat.Body.Voice;
using AlleyCat.Character;
using AlleyCat.Context;
using AlleyCat.Control.Locomotion;
using AlleyCat.Core;
using AlleyCat.Core.Content;
using AlleyCat.Navigation;
using AlleyCat.Rigging;
using AlleyCat.Scene;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

using CharacterHub = AlleyCat.Character.Character;

namespace AlleyCat.IntegrationTests.Characters;

/// <summary>
/// Godot-runtime coverage for authored visual cues, contextual descriptions, and character installation.
/// </summary>
public sealed class VisualCueIntegrationTests
{
    private const string ReferenceFemaleBaseScenePath =
        "res://assets/characters/templates/reference_female/reference_female_base.tscn";
    private const string ReferenceMaleBaseScenePath =
        "res://assets/characters/templates/reference_male/reference_male_base.tscn";
    private const string AllyNPCScenePath = "res://assets/characters/reference/ally_npc.tscn";
    private const string AllyPlayerScenePath = "res://assets/characters/reference/ally_player.tscn";
    private const string VadimNPCScenePath = "res://assets/characters/reference/vadim_npc.tscn";
    private const string AllyDescription =
        "Ally is a slender Asian woman in her early twenties, standing about 160 centimetres tall. Her unassuming features and meek expression give her a quiet, gentle appearance.";
    private const string VadimDescription =
        "Vadim is a sturdily built European man in his late twenties, standing around 180 centimetres tall. Light blond hair and pale skin frame an austere face, while his icy blue eyes lend him a distinctly stern appearance.";

    /// <summary>
    /// Shared female and male templates author exactly one referenced whole-character cue under Head.
    /// </summary>
    [Fact]
    public void ReferenceBaseTemplates_AuthorSingleReferencedBodyCueUnderHead()
    {
        AssertBaseTemplateCue(ReferenceFemaleBaseScenePath);
        AssertBaseTemplateCue(ReferenceMaleBaseScenePath);
    }

    /// <summary>
    /// Static cues retain origin sampling while their bounds supply scan extent samples.
    /// </summary>
    [Fact]
    public void StaticCue_WithNonPointBounds_ProvidesExtentScanSamplesAndReturnsGlobalPosition()
    {
        SceneTree sceneTree = GetSceneTree();
        var root = new Node3D
        {
            Position = new Vector3(2.5f, -1.0f, 4.0f),
            Rotation = new Vector3(0.0f, 0.6f, 0.0f),
        };
        var parent = new Node3D { Position = new Vector3(-0.5f, 3.0f, 1.25f) };
        var cue = new StaticVisualCue
        {
            Position = new Vector3(0.25f, 0.75f, -2.0f),
            Bounds = new SphereVisualBounds { Radius = 0.5f },
        };
        root.AddChild(parent);
        parent.AddChild(cue);
        sceneTree.Root.AddChild(root);

        try
        {
            Assert.Contains(cue.GetSampleLocalPositions(), sample => sample != Vector3.Zero);
            Assert.Equal(cue.GlobalPosition, cue.SampleGlobalPosition());
        }
        finally
        {
            root.QueueFree();
        }
    }

    /// <summary>
    /// Description rendering uses only its local root and exposes direct observer, cue, and subject values.
    /// </summary>
    [Fact]
    public void Describe_WithVisualSubjectAncestor_RendersLocalRootWithoutContextRequests()
    {
        SceneTree sceneTree = GetSceneTree();
        var observer = new TestVisualObserver("Observer Context");
        var subject = new TestVisualSubject("Subject Context");
        var cue = new StaticVisualCue
        {
            Description = "observer={{observer.Label}}; cue={{cue.ID}}; subject={{subject.Id}}",
        };
        subject.AddChild(cue);
        subject.VisualCues = [cue];
        sceneTree.Root.AddChild(subject);

        try
        {
            string description = cue.Describe(EmptySceneContext.Instance, observer);

            Assert.Equal("observer=Observer Context; cue=; subject=subject_context", description);
            Assert.Equal(0, observer.ContextRequestCount);
            Assert.Equal(0, subject.ContextRequestCount);
        }
        finally
        {
            subject.QueueFree();
        }
    }

    /// <summary>
    /// A cue without a visual-subject ancestor renders its local observer while leaving subject absent.
    /// </summary>
    [Fact]
    public void Describe_WithoutVisualSubjectAncestor_RendersObserverAndOmitsSubject()
    {
        SceneTree sceneTree = GetSceneTree();
        var observer = new TestVisualObserver("Observer Only");
        var root = new Node3D();
        var cue = new StaticVisualCue
        {
            Description = "observer={{observer.Label}}; {{#if subject}}subject-present{{else}}subject-absent{{/if}}",
        };
        root.AddChild(cue);
        sceneTree.Root.AddChild(root);

        try
        {
            string description = cue.Describe(EmptySceneContext.Instance, observer);

            Assert.Equal("observer=Observer Only; subject-absent", description);
            Assert.Equal(0, observer.ContextRequestCount);
        }
        finally
        {
            root.QueueFree();
        }
    }

    /// <summary>
    /// Production installation reconciles one cue per character and preserves each local description override.
    /// </summary>
    [Fact]
    public void ProductionCharacters_AfterInstallation_RenderApprovedDescriptionsWithoutDuplicateCues()
    {
        AssertInstalledCharacterDescription(AllyNPCScenePath, AllyDescription);
        AssertInstalledCharacterDescription(AllyPlayerScenePath, AllyDescription);
        AssertInstalledCharacterDescription(VadimNPCScenePath, VadimDescription);
    }

    /// <summary>
    /// Character provider validation rejects null references, blank IDs, and ordinal duplicate IDs.
    /// </summary>
    [Fact]
    public void CharacterProviderValidation_RejectsInvalidReferencesAndIDs()
    {
        AssertInvalidVisualCues([null!], "null visual cue reference");
        AssertInvalidVisualCues([CreateCue(" ", 1.0f)], "non-empty ID");
        AssertInvalidVisualCues(
            [CreateCue("body", 1.0f), CreateCue("body", 2.0f)],
            "duplicate visual cue ID 'body'");
    }

    /// <summary>
    /// Character provider validation rejects every non-finite or negative prominence category.
    /// </summary>
    [Fact]
    public void CharacterProviderValidation_RejectsInvalidProminence()
    {
        foreach (float prominence in new[] { -0.01f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            AssertInvalidVisualCues([CreateCue("body", prominence)], "finite, non-negative prominence");
        }
    }

    /// <summary>
    /// Character provider validation accepts disabled, unbounded finite, and ordinally case-distinct cues.
    /// </summary>
    [Fact]
    public void CharacterProviderValidation_AcceptsValidBoundaryValuesAndCaseDistinctIDs()
    {
        StaticVisualCue disabled = CreateCue("body", 0.0f);
        StaticVisualCue prominent = CreateCue("Body", 12.0f);
        CharacterHub character = CreateAuthoredCharacter([disabled, prominent]);
        try
        {
            character.RefreshComponents();

            Assert.Equal([disabled, prominent], character.VisualCues);
        }
        finally
        {
            character.Free();
        }
    }

    /// <summary>Publication snapshots authored cue topology until the character explicitly refreshes it.</summary>
    [Fact]
    public void CharacterProviderPublication_RetainsTopologyUntilExplicitRefresh()
    {
        StaticVisualCue publishedCue = CreateCue("published", 1.0f);
        StaticVisualCue replacementCue = CreateCue("replacement", 1.0f);
        CharacterHub character = CreateAuthoredCharacter([publishedCue]);
        character.AddChild(replacementCue);
        try
        {
            character.RefreshComponents();
            character.AuthoredVisualCues[0] = replacementCue;

            Assert.Same(publishedCue, Assert.Single(character.VisualCues));

            character.RefreshComponents();

            Assert.Same(replacementCue, Assert.Single(character.VisualCues));
        }
        finally
        {
            character.Free();
        }
    }

    /// <summary>Character refresh rejects an authored cue whose nearest provider is a nested character.</summary>
    [Fact]
    public void CharacterProviderValidation_RejectsCueOwnedByNestedProvider()
    {
        StaticVisualCue cue = CreateCue("body", 1.0f);
        CharacterHub outerCharacter = CreateAuthoredCharacter([cue]);
        var nestedCharacter = new CharacterHub { Name = "NestedCharacter" };
        outerCharacter.AddChild(nestedCharacter);
        outerCharacter.RemoveChild(cue);
        nestedCharacter.AddChild(cue);

        try
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(outerCharacter.RefreshComponents);

            Assert.Contains("nearest IProvidesVisualCues ancestor", exception.Message, StringComparison.Ordinal);
            Assert.Contains("NestedCharacter", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            outerCharacter.Free();
        }
    }

    private static void AssertBaseTemplateCue(string scenePath)
    {
        CharacterHub character = Assert.IsType<CharacterHub>(LoadPackedScene(scenePath).Instantiate(), exactMatch: false);
        try
        {
            StaticVisualCue cue = Assert.IsType<StaticVisualCue>(Assert.Single(character.AuthoredVisualCues), exactMatch: false);
            Assert.Equal("body", cue.ID);
            Assert.Equal(1.0f, cue.Prominence);
            Assert.True(character.IsInGroup("VisualSubjects"));
            Assert.Equal("Head", cue.GetParent().Name.ToString());
            Assert.Same(cue, Assert.Single(FindDescendants<StaticVisualCue>(character)));
        }
        finally
        {
            character.QueueFree();
        }
    }

    private static void AssertInstalledCharacterDescription(string scenePath, string expectedDescription)
    {
        CharacterHub character = Assert.IsType<CharacterHub>(LoadPackedScene(scenePath).Instantiate(), exactMatch: false);
        try
        {
            EnsureCharacterRuntimeInstalled(character);

            Assert.True(character.IsInGroup("VisualSubjects"));
            StaticVisualCue authoredCue = Assert.IsType<StaticVisualCue>(Assert.Single(character.AuthoredVisualCues), exactMatch: false);
            StaticVisualCue exposedCue = Assert.IsType<StaticVisualCue>(Assert.Single(character.VisualCues), exactMatch: false);
            StaticVisualCue descendantCue = Assert.Single(FindDescendants<StaticVisualCue>(character));
            Assert.Same(authoredCue, exposedCue);
            Assert.Same(authoredCue, descendantCue);
            Assert.Equal("body", authoredCue.ID);
            Assert.Equal(1.0f, authoredCue.Prominence);
            Assert.Equal("Head", authoredCue.GetParent().Name.ToString());

            var scene = new SceneContext([character]);
            Assert.Equal(expectedDescription, authoredCue.Describe(scene, character));
        }
        finally
        {
            character.QueueFree();
        }
    }

    private static void AssertInvalidVisualCues(VisualCue[] cues, string expectedMessage)
    {
        CharacterHub character = CreateAuthoredCharacter(cues);
        try
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(character.RefreshComponents);

            Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            character.Free();
        }
    }

    private static StaticVisualCue CreateCue(string id, float prominence)
        => new()
        {
            ID = id,
            Prominence = prominence,
        };

    private static CharacterHub CreateAuthoredCharacter(VisualCue[] cues)
    {
        var character = new CharacterHub { Name = "CharacterRoot", AuthoredVisualCues = cues };
        var locomotion = new CharacterLocomotion { Name = "Locomotion" };
        var navigation = new DirectTransformNavigation { Name = "Navigation" };
        var eyes = new EyesBehaviour { Name = "Eyes" };
        var voice = new TestVoice { Name = "Voice", Id = "Voice" };
        var leftHand = new HandPoseBehaviour { Name = "LeftHand", Side = LimbSide.Left };
        var rightHand = new HandPoseBehaviour { Name = "RightHand", Side = LimbSide.Right };
        character.AddChild(locomotion);
        character.AddChild(navigation);
        character.AddChild(eyes);
        character.AddChild(voice);
        character.AddChild(leftHand);
        character.AddChild(rightHand);
        foreach (VisualCue? cue in cues)
        {
            if (cue is not null)
            {
                character.AddChild(cue);
            }
        }

        character.Locomotion = locomotion;
        character.Navigation = navigation;
        character.Eyes = eyes;
        character.Voice = voice;
        character.LeftHand = leftHand;
        character.RightHand = rightHand;
        return character;
    }

    private static IReadOnlyList<T> FindDescendants<T>(Node root)
        where T : Node
    {
        List<T> matches = [];
        AddDescendants(root, matches);
        return matches;
    }

    private static void AddDescendants<T>(Node root, ICollection<T> matches)
        where T : Node
    {
        foreach (Node child in root.GetChildren())
        {
            if (child is T match)
            {
                matches.Add(match);
            }

            AddDescendants(child, matches);
        }
    }

    private sealed class TestVisualObserver(string label) : IEyesHolder, IContextual
    {
        public IReadOnlyList<IComponent> Components { get; } = [];

        public string Label => label;

        public int ContextRequestCount
        {
            get; private set;
        }

        public IReadOnlyDictionary<string, object?> GetContext(ISceneContext scene, IContextual? observer)
        {
            ContextRequestCount++;
            throw new InvalidOperationException("Visual cue rendering must not request observer context.");
        }
    }

    private sealed partial class TestVisualSubject(string label) : Node3D, IVisualSubject, IContextual
    {
        public string Id { get; set; } = label.ToLowerInvariant().Replace(' ', '_');

        public string Type => "test";

        public string Label => label;

        public IReadOnlyList<VisualCue> VisualCues { get; set; } = [];

        public int ContextRequestCount
        {
            get; private set;
        }

        public IReadOnlyDictionary<string, object?> GetContext(ISceneContext scene, IContextual? observer)
        {
            ContextRequestCount++;
            throw new InvalidOperationException("Visual cue rendering must not request subject context.");
        }
    }

    private sealed partial class TestVoice : Voice
    {
        public override void Speak(string speech)
        {
        }
    }

    private sealed record EmptySceneContext : ISceneContext
    {
        public static EmptySceneContext Instance { get; } = new();

        public IReadOnlyCollection<ICharacter> Characters { get; } = [];

        public ContentContext Content => ContentContext.Default;

        public IIdentifiable? Find(string fullId)
        {
            IdentityValidator.ValidateFullId(fullId, nameof(fullId));
            return null;
        }

        public IIdentifiable Resolve(string fullId)
            => throw new InvalidOperationException($"Current scene does not contain identifiable object '{fullId}'.");
    }
}
