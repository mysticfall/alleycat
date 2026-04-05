namespace AlleyCat.TestFramework;

/// <summary>
/// Controls whether an integration test runs in headless mode.
/// When applied with <see cref="Enabled"/> set to <c>false</c>, the Godot process
/// will be launched without the <c>--headless</c> flag, opening a visible window.
/// </summary>
/// <remarks>
/// <para>Resolution order (first match wins):</para>
/// <list type="number">
///   <item>Method-level <see cref="HeadlessAttribute"/>.</item>
///   <item>Class-level <see cref="HeadlessAttribute"/>.</item>
///   <item>Default: non-headless / windowed (<c>false</c>).</item>
/// </list>
/// <para>The global <c>--headless</c> CLI flag overrides all attribute settings.</para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class HeadlessAttribute : Attribute
{
    /// <summary>
    /// Initialises a new instance with headless enabled (default).
    /// </summary>
    public HeadlessAttribute()
    {
        Enabled = true;
    }

    /// <summary>
    /// Initialises a new instance with explicit headless control.
    /// </summary>
    /// <param name="enabled"><c>true</c> for headless mode; <c>false</c> for windowed mode.</param>
    public HeadlessAttribute(bool enabled)
    {
        Enabled = enabled;
    }

    /// <summary>
    /// Gets whether headless mode is enabled for the annotated test.
    /// </summary>
    public bool Enabled
    {
        get;
    }
}
