using System.Globalization;
using System.Text.Json;
using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Tool;
using AlleyCat.Mind.Observation;
using AlleyCat.Vision;
using Godot;
using Microsoft.Extensions.AI;
using Xunit;

namespace AlleyCat.Tests.Mind.AI.Tool;

/// <summary>
/// Deterministic coverage of the coordinate navigation contracts (AI-015 TR-1–TR-3, TR-5, TR-6, TR-15, TR-16,
/// TR-19): the shared bearing conversion and normalisation, pace mapping, the shared own-position text, the
/// normative tool names, descriptions and parameter schemas, the navigation-capability composition gate, and the
/// outcome observation's delivery shape.
/// </summary>
public sealed class CoordinateNavigationContractTests
{
    /// <summary>Bearings 0, 90, 180 and 270 face −Z, +X, +Z and −X, and convert back by the inverse (TR-5).</summary>
    [Theory]
    [InlineData(0d, 0f, -1f)]
    [InlineData(90d, 1f, 0f)]
    [InlineData(180d, 0f, 1f)]
    [InlineData(270d, -1f, 0f)]
    public void Bearing_CardinalValuesMapToWorldFacingAndBack(double bearing, float x, float z)
    {
        Vector3 facing = CompassBearing.ToWorldFacing(bearing);

        Assert.True(facing.DistanceTo(new Vector3(x, 0f, z)) < 1e-6f, $"Bearing {bearing} faced {facing}.");
        Assert.True((-CompassBearing.ToBasis(bearing).Z).DistanceTo(facing) < 1e-6f);
        Assert.True(CompassBearing.TryFromWorldFacing(facing, out double back));
        Assert.Equal(bearing, back, 6);
    }

    /// <summary>
    /// A navigation yaw converts to the bearing of the direction it faces: yaw −90° faces +X (bearing 90) and yaw
    /// +90° faces −X (bearing 270), matching NAV-001's yaw convention atan2(−facing.x, −facing.z) (TR-5).
    /// </summary>
    [Theory]
    [InlineData(-90d, 90d)]
    [InlineData(90d, 270d)]
    [InlineData(180d, 180d)]
    [InlineData(0d, 0d)]
    public void Bearing_FromNavigationYaw(double yawDegrees, double expectedBearing)
        => Assert.Equal(expectedBearing, CompassBearing.FromYawRadians(yawDegrees * Math.PI / 180d), 6);

    /// <summary>
    /// Any finite heading normalises into [0, 360) — negative, exactly 360 and above — and non-finite values are
    /// refused (TR-5).
    /// </summary>
    [Theory]
    [InlineData(-90d, 270d)]
    [InlineData(360d, 0d)]
    [InlineData(720.5d, 0.5d)]
    [InlineData(-720d, 0d)]
    [InlineData(-0d, 0d)]
    [InlineData(359.25d, 359.25d)]
    public void Bearing_NormalisesFiniteValues(double bearing, double expected)
    {
        Assert.True(CompassBearing.TryNormalise(bearing, out double normalised));
        Assert.Equal(expected, normalised, 9);
        Assert.InRange(normalised, 0d, 359.999999999d);
        Assert.False(double.IsNegative(normalised));
    }

    /// <summary>Non-finite bearings are refused (TR-5).</summary>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Bearing_RefusesNonFiniteValues(double bearing)
    {
        Assert.False(CompassBearing.TryNormalise(bearing, out _));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => CompassBearing.Normalise(bearing));
    }

    /// <summary>
    /// Paces map to the documented normalised speeds, a missing pace defaults to <c>walk</c>, and an unknown pace is
    /// refused (TR-6).
    /// </summary>
    [Theory]
    [InlineData("stroll", "stroll", 0.5f)]
    [InlineData("walk", "walk", 0.75f)]
    [InlineData("hurry", "hurry", 1.0f)]
    [InlineData(null, "walk", 0.75f)]
    [InlineData("", "walk", 0.75f)]
    public void Pace_MapsNamesToNormalisedSpeedWithWalkDefault(string? name, string canonical, float speed)
    {
        Assert.True(NavigationPace.TryResolve(name, out string resolved, out float resolvedSpeed));
        Assert.Equal(canonical, resolved);
        Assert.Equal(speed, resolvedSpeed);
    }

    /// <summary>Raw speeds and unknown names are not paces (TR-6).</summary>
    [Theory]
    [InlineData("run")]
    [InlineData("0.5")]
    public void Pace_RefusesUnknownNames(string name)
        => Assert.False(NavigationPace.TryResolve(name, out _, out _));

    /// <summary>
    /// The shared own-position text rounds position to 0.1 m half away from zero, never prints negative zero, and
    /// rounds the heading to a whole degree in [0, 360), in invariant culture regardless of the current culture (TR-15).
    /// </summary>
    [Theory]
    [InlineData(1.25d, -3.96d, 89.5d, "You are at (1.3, -4.0), facing 90°.")]
    [InlineData(-0.04d, 0.04d, 359.6d, "You are at (0.0, 0.0), facing 0°.")]
    [InlineData(-12.35d, 7d, -90d, "You are at (-12.4, 7.0), facing 270°.")]
    [InlineData(100d, -0.05d, 0.4d, "You are at (100.0, -0.1), facing 0°.")]
    public void SelfPositionText_FormatsRoundedInvariantSentence(double x, double z, double bearing, string expected)
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal(expected, SelfPositionText.Format(x, z, bearing));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>The tool names and model-facing descriptions match AI-015 TR-2/TR-3 verbatim.</summary>
    [Fact]
    public void ToolNamesAndDescriptions_MatchTheNormativeTexts()
    {
        Assert.Equal("walk_to", WalkToTool.ProductionToolName);
        Assert.Equal("turn_to", TurnToTool.ProductionToolName);
        Assert.Equal("stop", StopTool.ProductionToolName);
        Assert.Equal(
            "Start walking to a spot on the ground. Give the destination as absolute world coordinates read from "
            + "the minimap grid labels: `x` in metres (increases to the right on the map) and `z` in metres "
            + "(increases downwards on the map). Optional `heading` is the direction to face once you arrive, as a "
            + "compass bearing in degrees: 0 = up on the map (−Z), 90 = right (+X), 180 = down (+Z), 270 = left "
            + "(−X), measured clockwise; any finite value is normalised into [0, 360). Without `heading` you keep "
            + "facing the way you were walking when you arrive. Optional `pace` is `stroll`, `walk` (default) or "
            + "`hurry`. Returns at once when the walk has started (or with a refusal if the spot cannot be "
            + "reached); you are told later when you arrive or if the walk fails. A new `walk_to` replaces any walk "
            + "already in progress. To face something you can only judge from your destination, such as a person "
            + "seen from where you will stand, leave `heading` out and call `turn_to` after you arrive.",
            WalkToTool.ProductionToolDescription);
        Assert.Equal(
            "Turn on the spot to face a direction, without walking. `heading` is a compass bearing in degrees: "
            + "0 = up on the map (−Z), 90 = right (+X), 180 = down (+Z), 270 = left (−X), measured clockwise; any "
            + "finite value is normalised into [0, 360). Returns at once when the turn has started.",
            TurnToTool.ProductionToolDescription);
        Assert.Equal(
            "Stop walking: cancels your current destination and you stand still where you are. Returns at once.",
            StopTool.ProductionToolDescription);
    }

    /// <summary>
    /// The model-facing schemas expose exactly the normative parameters with their TR-3 texts: <c>walk_to</c> takes
    /// required <c>x</c>/<c>z</c>, optional <c>heading</c> and a <c>pace</c> enum of the three names; <c>turn_to</c>
    /// takes required <c>heading</c>; <c>stop</c> takes nothing. No identity or target-name parameter exists
    /// (TR-1–TR-3).
    /// </summary>
    [Fact]
    public void ToolSchemas_ExposeOnlyTheNormativeParameters()
    {
        CoordinateNavigationToolFunctions functions = new(
            new CoordinateNavigationController(new LifecycleNavigation(), new ScriptedNavigationEnvironment(), static _ => { }));

        JsonElement walk = AIFunctionFactory.Create(functions.WalkTo).JsonSchema;
        JsonElement turn = AIFunctionFactory.Create(functions.TurnTo).JsonSchema;
        JsonElement stop = AIFunctionFactory.Create(functions.Stop).JsonSchema;

        Assert.Equal(["x", "z", "heading", "pace"], PropertyNames(walk));
        Assert.Equal(["x", "z"], RequiredNames(walk));
        Assert.Equal("Destination world X in metres, as labelled on the minimap grid.", Description(walk, "x"));
        Assert.Equal("Destination world Z in metres, as labelled on the minimap grid.", Description(walk, "z"));
        Assert.Equal(
            "Optional compass bearing in degrees to face on arrival. It is the direction to face after arriving, "
            + "judged from the destination point on the map, not from where you stand now.",
            Description(walk, "heading"));
        Assert.Equal("Optional walking pace.", Description(walk, "pace"));
        Assert.Equal(
            ["stroll", "walk", "hurry"],
            walk.GetProperty("properties").GetProperty("pace").GetProperty("enum").EnumerateArray()
                .Where(static value => value.ValueKind == JsonValueKind.String)
                .Select(static value => value.GetString()!));

        Assert.Equal(["heading"], PropertyNames(turn));
        Assert.Equal(["heading"], RequiredNames(turn));
        Assert.Equal("Compass bearing in degrees to face.", Description(turn, "heading"));

        Assert.Empty(PropertyNames(stop));
    }

    /// <summary>
    /// A model call through the schema-bound function reaches the controller: an unknown pace comes back as a
    /// contained refusal text rather than an exception (TR-6, TR-10).
    /// </summary>
    [Fact]
    public async Task WalkToFunction_ReturnsContainedRefusalForUnknownPace()
    {
        LifecycleNavigation navigation = new();
        CoordinateNavigationToolFunctions functions = new(
            new CoordinateNavigationController(navigation, new ScriptedNavigationEnvironment(), static _ => { }));
        // The production tool factory marshals the delegate result unchanged (AgentTool.CreateFunction).
        AIFunction walk = AIFunctionFactory.Create(functions.WalkTo, new AIFunctionFactoryOptions
        {
            MarshalResult = static (value, _, _) => ValueTask.FromResult(value),
        });

        object? result = await walk.InvokeAsync(new AIFunctionArguments
        {
            ["x"] = 1.5,
            ["z"] = -2,
            ["pace"] = "sprint",
        });

        AgentToolResult toolResult = Assert.IsType<AgentToolResult>(result);
        Assert.StartsWith("Walk refused: unknown pace 'sprint'", toolResult.Message, StringComparison.Ordinal);
        Assert.Empty(toolResult.Observations);
        Assert.Null(toolResult.Image);
        Assert.Empty(navigation.Submitted);
    }

    /// <summary>
    /// The composition gate resolves navigation only for a subject exposing exactly one <c>INavigation</c>
    /// component (TR-1).
    /// </summary>
    [Fact]
    public void CompositionGate_ResolvesNavigationOnlyForNavigationCapableSubjects()
    {
        LifecycleNavigation navigation = new();

        Assert.True(AgenticMind.TryResolveNavigation(new ComponentCharacter(navigation), out AlleyCat.Navigation.INavigation? resolved));
        Assert.Same(navigation, resolved);
        Assert.False(AgenticMind.TryResolveNavigation(new ComponentCharacter(), out AlleyCat.Navigation.INavigation? absent));
        Assert.Null(absent);
    }

    /// <summary>
    /// Outcomes are ordinary event-timeline observations: never-expiring and event-eligible under the default
    /// policy, claiming no fresh turn, so AI-002 delivery and wake behaviour apply unchanged (TR-14).
    /// </summary>
    [Fact]
    public void Outcome_IsAnOrdinaryEventEligibleObservation()
    {
        ObservedNavigationOutcome outcome = new(NavigationOutcomeKind.Replaced, CoordinateMoveKind.Walk, null, 1, 2, 3);
        ObservationContext context = new(new ComponentCharacter());
        IObservationLifetimePolicy policy = new ObservationLifetimePolicyRegistry(
            InitialObservationLifetimePolicies.Create()).Resolve(typeof(ObservedNavigationOutcome));

        Assert.Equal("navigation.outcome", outcome.TypeKey);
        Assert.False(outcome.RequiresFreshTurn(context));
        Assert.True(policy.IsEventEligible(outcome));
        Assert.False(policy.HasFiniteLifetime);
        _ = Assert.Throws<ArgumentException>(
            () => new ObservedNavigationOutcome(NavigationOutcomeKind.Failed, CoordinateMoveKind.Walk, null, 0, 0, 0));
        _ = Assert.Throws<ArgumentException>(
            () => new ObservedNavigationOutcome(NavigationOutcomeKind.Arrived, CoordinateMoveKind.Turn, "x", 0, 0, 0));
    }

    /// <summary>
    /// Only <c>arrived</c> and <c>failed</c> outcomes wake a parked agent; <c>stopped</c> and <c>replaced</c> follow
    /// the NPC's own call, and existing observation kinds keep the non-waking default (AI-015 TR-14 as amended).
    /// </summary>
    [Fact]
    public void Wake_OnlyArrivedAndFailedOutcomesOptIn()
    {
        ObservationContext context = new(new ComponentCharacter());

        Assert.True(new ObservedNavigationOutcome(NavigationOutcomeKind.Arrived, CoordinateMoveKind.Walk, null, 0, 0, 0).WakesWaitingAgent(context));
        Assert.True(new ObservedNavigationOutcome(NavigationOutcomeKind.Failed, CoordinateMoveKind.Turn, "blocked", 0, 0, 0).WakesWaitingAgent(context));
        Assert.False(new ObservedNavigationOutcome(NavigationOutcomeKind.Stopped, CoordinateMoveKind.Walk, null, 0, 0, 0).WakesWaitingAgent(context));
        Assert.False(new ObservedNavigationOutcome(NavigationOutcomeKind.Replaced, CoordinateMoveKind.Walk, null, 0, 0, 0).WakesWaitingAgent(context));
        Assert.False(new ObservedSpeech("char:other", "Over here!", ["char:subject"]).WakesWaitingAgent(context));
        Assert.False(new ObservedVisualPresence("char:other").WakesWaitingAgent(context));
        Assert.False(new ObservedVisualDescription("char:other", "A person.").WakesWaitingAgent(context));
    }

    /// <summary>
    /// The AI minimap composition requests the fixed 40 px grid spacing override (TR-17), and its capture
    /// description carries no own-position sentence (TR-16).
    /// </summary>
    [Fact]
    public void AIMinimap_RequestsTheGridOverrideAndCarriesNoOwnPositionText()
    {
        Assert.Equal(40f, VisualContextCapture.ComposeMinimapSettings("char:subject").GridMinimumSpacingPixels);

        string description = VisualContextImage.Minimap([1], 1.5, -2, radiusMetres: 100, gameTimeSeconds: 3)
            .DescribeCapture();

        Assert.DoesNotContain("You are at", description, StringComparison.Ordinal);
        Assert.DoesNotContain("facing", description, StringComparison.Ordinal);
    }

    private static IEnumerable<string> PropertyNames(JsonElement schema)
        => schema.TryGetProperty("properties", out JsonElement properties)
            ? properties.EnumerateObject().Select(static property => property.Name)
            : [];

    private static IEnumerable<string> RequiredNames(JsonElement schema)
        => schema.TryGetProperty("required", out JsonElement required)
            ? required.EnumerateArray().Select(static name => name.GetString()!)
            : [];

    private static string? Description(JsonElement schema, string parameter)
        => schema.GetProperty("properties").GetProperty(parameter).GetProperty("description").GetString();

    private sealed class ComponentCharacter(params IComponent[] components) : ICharacter
    {
        public string Id { get; set; } = "subject";

        public IReadOnlyList<IComponent> Components { get; } = components;

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public Transform3D GlobalTransform { get; set; } = Transform3D.Identity;
    }
}
