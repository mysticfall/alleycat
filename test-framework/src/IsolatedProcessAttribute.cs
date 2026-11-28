namespace AlleyCat.TestFramework;

/// <summary>
/// Marks an integration test that needs a fresh Godot process of its own, for example because it loads a real
/// in-process model, configures process-wide native state that can be set only once, or observes every process's
/// activity on the host.
/// </summary>
/// <remarks>
/// <para>Isolated tests take part in discovery and execution like any other test: default runs, <c>--test-class</c>
/// runs, exact <c>--test-method</c> selectors and UID filters all select them when they match.</para>
/// <para>The runner shuts down every live session before an isolated test, runs it in a newly started session, and
/// shuts that session down afterwards, so no other Godot session process runs alongside it and every isolated test
/// gets its own process.</para>
/// <para>Method-level and class-level markers combine with OR, and the class marker is inherited.</para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class IsolatedProcessAttribute : Attribute
{
}
