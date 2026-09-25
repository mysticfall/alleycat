namespace AlleyCat.TestFramework;

/// <summary>
/// Marks an integration test as requiring live LLM access, making it subject to the
/// opt-in <c>--live-llm</c> command-line gate.
/// </summary>
/// <remarks>
/// <para>Live-marked tests are excluded from discovery and execution unless the
/// <c>--live-llm</c> command-line flag is supplied. An excluded test produces no discovered
/// node, no in-progress node, and no terminal result node.</para>
/// <para>Method-level and class-level markers combine with OR: a test is live-marked when
/// either the method or its declaring class carries the attribute. A method declared on a
/// class within a marked inheritance hierarchy is live-marked through its declaring type.</para>
/// <para>The <c>--live-llm</c> flag only permits live-marked tests; ordinary tests remain
/// selected by the existing filters whether or not the flag is supplied.</para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class LiveLlmAttribute : Attribute
{
}
