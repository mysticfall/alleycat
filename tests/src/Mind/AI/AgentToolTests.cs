using AlleyCat.Mind.AI.Tool;
using AlleyCat.Mind.Observation;
using Microsoft.Extensions.AI;
using Xunit;
using AgentObservation = AlleyCat.Mind.Observation.Observation;

namespace AlleyCat.Tests.Mind.AI;

/// <summary>
/// Unit coverage for action-tool result contracts and metadata.
/// </summary>
public sealed class AgentToolTests
{
    /// <summary>
    /// Result envelopes own an immutable ordered snapshot and permit optional messages and empty observations.
    /// </summary>
    [Fact]
    public void AgentToolResult_SnapshotsOrderedObservationsAndAllowsEmptyState()
    {
        List<AgentObservation> source =
        [
            new TestObservation("first"),
            new TestObservation("second"),
        ];

        var populated = new AgentToolResult("Done.", source);
        var empty = new AgentToolResult();
        source.Clear();

        Assert.Equal("Done.", populated.Message);
        Assert.Equal(["first", "second"], populated.Observations.Cast<TestObservation>().Select(x => x.Value));
        Assert.Null(empty.Message);
        Assert.Empty(empty.Observations);
    }

    /// <summary>
    /// Tool delegates with non-envelope return contracts are rejected before invocation.
    /// </summary>
    [Fact]
    public void CreateFunction_WithWrongDelegateResultType_FailsEarly()
    {
        var services = new EmptyServiceProvider();

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => AgentTool.CreateFunction(ToolHost.WrongResult, services));

        Assert.Contains(nameof(AgentToolResult), exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Tool resources pass authored metadata through to the generated AI function.
    /// </summary>
    [Fact]
    public void CreateFunction_WithResourceMetadata_UsesConfiguredNameAndDescription()
    {
        AIFunction function = AgentTool.CreateFunction(
            ToolHost.ValidResult,
            new EmptyServiceProvider(),
            "speak",
            "Speak aloud.");

        Assert.Equal("speak", function.Name);
        Assert.Equal("Speak aloud.", function.Description);
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private static class ToolHost
    {
        public static Task<string> WrongResult() => Task.FromResult("wrong");

        public static ValueTask<AgentToolResult> ValidResult() => ValueTask.FromResult(new AgentToolResult());
    }

    private sealed record TestObservation(string Value) : AgentObservation
    {
        public override string TypeKey => "test.tool-result";

        public override float CalculateImportance(ObservationContext context) => 1f;
    }
}
