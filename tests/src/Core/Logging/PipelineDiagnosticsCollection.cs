using Xunit;

namespace AlleyCat.Tests.Core.Logging;

/// <summary>
/// Collection serialising every unit-test class that drives the static <c>PipelineDebugLog</c> logger-factory
/// override, so concurrent installs and restores can never observe a missing factory.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PipelineDiagnosticsCollection
{
    /// <summary>The shared collection name for pipeline-diagnostics state isolation.</summary>
    public const string Name = "Pipeline diagnostics";
}
