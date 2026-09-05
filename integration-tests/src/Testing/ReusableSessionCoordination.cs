namespace AlleyCat.IntegrationTests.Testing;

/// <summary>
/// Deliberate cross-test coordination state for the reusable-session contract tests.
/// </summary>
/// <remarks>
/// <para>
/// CLR statics are not reset between tests inside one reusable session process; that documented
/// test-owned cleanup limit is exactly what these markers rely on, so they are intentionally never
/// reset by the tests.
/// </para>
/// <para>
/// They are inert: plain values held in test-assembly statics with no consumer outside
/// <see cref="ReusableSessionIntegrationTests"/> and
/// <see cref="ReusableSessionIsolatedGameIntegrationTests"/>.
/// </para>
/// </remarks>
internal static class ReusableSessionCoordination
{
    /// <summary>Marker value written by the first static-sharing writer test to run.</summary>
    public const string FirstWriterMarker = "reusable-session-first-writer-marker";

    /// <summary>Marker value written by the second static-sharing writer test to run.</summary>
    public const string SecondWriterMarker = "reusable-session-second-writer-marker";

    /// <summary>
    /// One GUID recorded per test-class construction, proving each coordinating test body runs on a
    /// freshly constructed fixture instance inside the shared session.
    /// </summary>
    public static readonly List<Guid> FixtureInstanceIds = [];

    /// <summary>
    /// Session-static string shared by the two static-sharing tests; the second test to run reads
    /// the first test's value, proving the documented no-CLR-isolation contract.
    /// </summary>
    public static string? SharedStaticMarker
    {
        get;
        set;
    }

    /// <summary>Set by the polluting test once it has completed; guards its baseline checker.</summary>
    public static bool PollutingTestCompleted
    {
        get;
        set;
    }

    /// <summary>
    /// Set by the isolated-<c>Game</c> test once it has completed; guards its
    /// <c>Global</c>-restoration checker in <see cref="ReusableSessionIntegrationTests"/>.
    /// </summary>
    public static bool IsolatedGameTestCompleted
    {
        get;
        set;
    }
}
