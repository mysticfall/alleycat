using Xunit;

namespace AlleyCat.Tests.Mind.AI;

/// <summary>
/// Collection serialising every unit-test class that drives the static mind session transcript recorder test
/// seams — the root-path override and turn-counter reset — so concurrent installs, restores, and recorder
/// deactivations can never observe another class's redirected root.
/// </summary>
[CollectionDefinition(Name)]
public sealed class MindSessionTranscriptSeamCollection
{
    /// <summary>The shared collection name for mind session transcript state isolation.</summary>
    public const string Name = "Mind session transcript seams";
}
