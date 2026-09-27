using System.Reflection;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Tool;
using Xunit;

namespace AlleyCat.Tests.Mind.AI;

/// <summary>
/// Unit coverage for the read-only lore tool's model-facing surface: the delegate exposes only the entry-ID list as
/// model input next to the trusted context binding (AI-002 TR-23).
/// </summary>
public sealed class ReadLoreToolTests
{
    /// <summary>
    /// The retrieval delegate's parameters are the trusted session context, the entry-ID list, and the
    /// cancellation token; the shared tool factory excludes the context from the model schema, so entry IDs are the
    /// only model-visible input.
    /// </summary>
    [Fact]
    public void ReadDelegate_BindsTrustedContextAndExposesOnlyEntryIDsAsModelInput()
    {
        MethodInfo? read = typeof(ReadLoreTool).GetMethod("Read", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(read);
        Assert.Equal(typeof(ValueTask<AgentToolResult>), read.ReturnType);

        ParameterInfo[] parameters = read.GetParameters();
        Assert.Equal(3, parameters.Length);
        Assert.Equal(typeof(ScenarioContext), parameters[0].ParameterType);
        Assert.Equal(typeof(IReadOnlyList<string>), parameters[1].ParameterType);
        Assert.Equal("entry_ids", parameters[1].Name);
        Assert.Equal(typeof(CancellationToken), parameters[2].ParameterType);
    }

    /// <summary>The production lore tool keeps the model-facing function name stable (AI-002 TR-13).</summary>
    [Fact]
    public void ProductionToolName_IsStable()
        => Assert.Equal("read_lore", ReadLoreTool.ProductionToolName);
}
