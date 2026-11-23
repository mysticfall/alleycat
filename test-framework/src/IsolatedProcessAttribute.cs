namespace AlleyCat.TestFramework;

/// <summary>
/// Marks an integration test that needs a fresh Godot process of its own, for example because it configures
/// process-wide native state that can be set only once, or observes every process's activity on the host.
/// </summary>
/// <remarks>
/// <para>Isolated tests are opt-in: they are excluded from discovery and execution unless an exact
/// <c>--test-method</c> selector names them. Default runs, <c>--test-class</c> runs and UID filters never select
/// them; an excluded test produces no discovered, in-progress, or terminal node.</para>
/// <para>When selected, the runner shuts down every live session before the test, runs it in a newly started
/// session, and shuts that session down afterwards, so no other Godot session process runs alongside it and
/// several isolated tests named in one run each get their own process.</para>
/// <para>Method-level and class-level markers combine with OR, and the class marker is inherited.</para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class IsolatedProcessAttribute : Attribute
{
}
