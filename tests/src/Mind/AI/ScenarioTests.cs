using System.Reflection;
using AlleyCat.Character;
using AlleyCat.Context;
using AlleyCat.Core;
using AlleyCat.Mind.AI;
using AlleyCat.Scene;
using AlleyCat.Vision;
using Xunit;

namespace AlleyCat.Tests.Mind.AI;

/// <summary>
/// Unit coverage for the scenario record, manager contract, and reserved render-dictionary key.
/// </summary>
public sealed class ScenarioTests
{
    /// <summary>The scenario is a plain sealed record exposing exactly one authored description property.</summary>
    [Fact]
    public void Scenario_IsAPlainSealedRecordWithExactlyOneDescriptionProperty()
    {
        Type scenarioType = typeof(Scenario);

        Assert.True(scenarioType.IsSealed);
        Assert.Equal(typeof(object), scenarioType.BaseType);

        PropertyInfo[] declaredProperties = scenarioType.GetProperties(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        PropertyInfo description = Assert.Single(declaredProperties);
        Assert.Equal(nameof(Scenario.Description), description.Name);
        Assert.Equal(typeof(string), description.PropertyType);
        Assert.True(description.CanRead);
    }

    /// <summary>The scenario description rejects null at construction.</summary>
    [Fact]
    public void Scenario_Constructor_RejectsNullDescription()
    {
        Assert.Equal(
            "Description",
            Assert.Throws<ArgumentNullException>(() => new Scenario(null!)).ParamName);
    }

    /// <summary>The manager contract exposes exactly the single turn-query member.</summary>
    [Fact]
    public void IScenarioManager_DefinesExactlyTheGetCurrentScenarioMember()
    {
        Type managerType = typeof(IScenarioManager);

        MethodInfo[] members = managerType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        MethodInfo query = Assert.Single(members);
        Assert.Equal(nameof(IScenarioManager.GetCurrentScenario), query.Name);
        Assert.Equal(typeof(Scenario), query.ReturnType);
        ParameterInfo[] parameters = query.GetParameters();
        ParameterInfo previous = Assert.Single(parameters);
        Assert.Equal("previous", previous.Name);
        Assert.Equal(typeof(ScenarioContext), previous.ParameterType);
        Assert.Empty(managerType.GetProperties(BindingFlags.Instance | BindingFlags.Public));
        Assert.False(typeof(IServiceProvider).IsAssignableFrom(managerType));
    }

    /// <summary>The reserved scenario key carries the turn's record or null alongside the other reserved keys.</summary>
    [Fact]
    public void CreateRenderContext_WithScenario_PublishesRecordOrNullUnderReservedKey()
    {
        FakeCharacter owner = new()
        {
            Id = "owner"
        };
        SceneContext scene = new([owner]);
        var scenario = new Scenario("The owner is browsing the market.");

        IReadOnlyDictionary<string, object?> withScenario = AgenticMind.CreateRenderContext(
            owner,
            scene,
            scenario: scenario);
        IReadOnlyDictionary<string, object?> withoutScenario = AgenticMind.CreateRenderContext(
            owner,
            scene,
            scenario: null);
        IReadOnlyDictionary<string, object?> defaulted = AgenticMind.CreateRenderContext(owner, scene);

        Assert.Same(scenario, withScenario["scenario"]);
        Assert.Null(withoutScenario["scenario"]);
        Assert.Null(defaulted["scenario"]);
        Assert.All(
            [withScenario, withoutScenario],
            context => Assert.Equal(["character", "characters", "observations", "scenario"], context.Keys));
    }

    private sealed class FakeCharacter : ICharacter
    {
        public string Id { get; set; } = "fake-character";

        public string FullId => $"char:{Id}";

        public IReadOnlyList<IComponent> Components { get; } = [];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public IReadOnlyDictionary<string, object?> GetContext(ISceneContext scene, IContextual? observer)
            => new Dictionary<string, object?>();
    }
}
