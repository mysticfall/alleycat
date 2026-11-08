using System.Reflection;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Tool;
using Xunit;

namespace AlleyCat.Tests.Mind.AI;

/// <summary>
/// Unit coverage for the model-facing surface of the two AI-013 visual-context tools (TR-2/TR-6): stable
/// production names, zero-model-input delegate shapes bound to the trusted session context, and shipped
/// descriptions whose accuracy claims — zero-argument shape, self-binding, returned image, contained failure,
/// ephemeral lifetime — match runtime behaviour, with no argument, persistence, or observation promises.
/// </summary>
public sealed class VisualContextToolContractTests
{
    /// <summary>The production visual tools keep their model-facing function names stable (AI-013 TR-2).</summary>
    [Fact]
    public void ProductionToolNames_AreStable()
    {
        Assert.Equal("minimap", MinimapTool.ProductionToolName);
        Assert.Equal("capture_view", CaptureViewTool.ProductionToolName);
    }

    /// <summary>
    /// Both delegates expose no model-facing inputs (AI-013 TR-2): the trusted session context binds through the
    /// composition factory's excluded parameter, and the cancellation token binds automatically — so the
    /// model-visible schema of each tool is empty.
    /// </summary>
    [Fact]
    public void Delegates_ExposeNoModelFacingInputs()
    {
        Type[] toolTypes = [typeof(MinimapTool), typeof(CaptureViewTool)];
        foreach (Type toolType in toolTypes)
        {
            MethodInfo? delegateMethod = FindInvokeDelegate(toolType);
            Assert.NotNull(delegateMethod);

            ParameterInfo[] parameters = delegateMethod.GetParameters();
            Assert.Equal(2, parameters.Length);
            Assert.Equal(typeof(ScenarioContext), parameters[0].ParameterType);
            Assert.Equal(typeof(CancellationToken), parameters[1].ParameterType);
            Assert.True(parameters[1].HasDefaultValue, "The cancellation token must stay optional.");
        }
    }

    /// <summary>
    /// The minimap description obeys AI-002 TR-15–TR-16 accuracy under AI-013 TR-6: it states the zero-argument
    /// shape, that the map is always centred on the calling NPC with its own marker highlighted, what it returns,
    /// that failures return an error result rather than an image, and that image content is ephemeral with a
    /// placeholder after the next scheduling turn. It claims no distance or framing argument and promises no
    /// observation or world effect.
    /// </summary>
    [Fact]
    public void MinimapDescription_MatchesRuntimeBehaviour()
    {
        string description = MinimapTool.ProductionToolDescription;

        Assert.Contains("Takes no arguments", description, StringComparison.Ordinal);
        Assert.Contains("always centred on your own current position", description, StringComparison.Ordinal);
        Assert.Contains("gold", description, StringComparison.Ordinal);
        Assert.Contains("*You*", description, StringComparison.Ordinal);
        Assert.Contains("Returns the rendered map image", description, StringComparison.Ordinal);
        Assert.Contains("error result instead of an image", description, StringComparison.Ordinal);
        Assert.Contains("ephemeral", description, StringComparison.Ordinal);
        Assert.Contains("replaced in your context by a short textual record", description, StringComparison.Ordinal);
        Assert.Contains("auto-scales", description, StringComparison.Ordinal);
        Assert.Contains("service default", description, StringComparison.Ordinal);
        Assert.Contains("observes nothing and changes nothing", description, StringComparison.Ordinal);
    }

    /// <summary>
    /// The view-capture description obeys the same accuracy rule (AI-013 TR-6): zero-argument shape, own-eyes
    /// binding, fresh capture per call, returned image, contained failure, and the ephemeral lifetime with
    /// re-capture guidance.
    /// </summary>
    [Fact]
    public void CaptureViewDescription_MatchesRuntimeBehaviour()
    {
        string description = CaptureViewTool.ProductionToolDescription;

        Assert.Contains("Takes no arguments", description, StringComparison.Ordinal);
        Assert.Contains("through your own eyes", description, StringComparison.Ordinal);
        Assert.Contains("captures a new frame", description, StringComparison.Ordinal);
        Assert.Contains("Returns the captured image", description, StringComparison.Ordinal);
        Assert.Contains("error result instead of an image", description, StringComparison.Ordinal);
        Assert.Contains("ephemeral", description, StringComparison.Ordinal);
        Assert.Contains("replaced in your context by a short textual record", description, StringComparison.Ordinal);
        Assert.Contains("observes nothing", description, StringComparison.Ordinal);
        Assert.Contains("changes nothing", description, StringComparison.Ordinal);
    }

    private static MethodInfo FindInvokeDelegate(Type toolType)
        => Assert.Single(
            toolType.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly),
            method => method.ReturnType == typeof(ValueTask<AgentToolResult>));
}
