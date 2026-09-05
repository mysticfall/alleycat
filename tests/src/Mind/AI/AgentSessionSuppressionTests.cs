using AlleyCat.Mind.AI;
using Xunit;

namespace AlleyCat.Tests.Mind.AI;

/// <summary>
/// Unit coverage for the <c>--no-ai</c> agent-session suppression switch: pure argument matching and the test-seam
/// state contract, kept free of Godot APIs so it runs without a Godot runtime.
/// </summary>
public sealed class AgentSessionSuppressionTests
{
    /// <summary>
    /// The switch literal is part of the CLI contract and must stay exactly <c>--no-ai</c>.
    /// </summary>
    [Fact]
    public void NoAISwitch_LiteralIsTheDocumentedCommandLineSwitch()
        => Assert.Equal("--no-ai", AgentSessionSuppression.NoAISwitch);

    /// <summary>
    /// An empty user-argument list never suppresses agent sessions.
    /// </summary>
    [Fact]
    public void ContainsSuppressionSwitch_WithoutArguments_ReturnsFalse()
        => Assert.False(AgentSessionSuppression.ContainsSuppressionSwitch([]));

    /// <summary>
    /// User arguments without the switch — mirroring the integration runner's arguments — never suppress agent
    /// sessions.
    /// </summary>
    [Fact]
    public void ContainsSuppressionSwitch_WithOtherUserArguments_ReturnsFalse()
    {
        Assert.False(AgentSessionSuppression.ContainsSuppressionSwitch(
            ["--integration-test-session", "--probe-assembly", "/tmp/assembly.dll", "--output-dir", "x"]));
    }

    /// <summary>
    /// The switch suppresses regardless of its position among other user arguments.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public void ContainsSuppressionSwitch_InAnyArgumentPosition_ReturnsTrue(int switchIndex)
    {
        string[] args = ["--integration-test-session", "foo", "--output-dir", "x", "--verbose"];
        args[switchIndex] = AgentSessionSuppression.NoAISwitch;

        Assert.True(AgentSessionSuppression.ContainsSuppressionSwitch(args));
    }

    /// <summary>
    /// Only the exact switch matches: case differences, single-dash spellings, and suffixed variants never
    /// suppress agent sessions.
    /// </summary>
    [Theory]
    [InlineData("--no-AI")]
    [InlineData("-no-ai")]
    [InlineData("--no-ai-extra")]
    [InlineData("no-ai")]
    public void ContainsSuppressionSwitch_WithNearMissSwitches_ReturnsFalse(string nearMiss)
        => Assert.False(AgentSessionSuppression.ContainsSuppressionSwitch([nearMiss]));

    /// <summary>
    /// A null argument list is a caller bug and must fail fast.
    /// </summary>
    [Fact]
    public void ContainsSuppressionSwitch_WithNullArguments_Throws()
        => Assert.Throws<ArgumentNullException>(
            () => _ = AgentSessionSuppression.ContainsSuppressionSwitch(null!));

    /// <summary>
    /// Forcing suppression through the test seam reports suppression without consulting user arguments, and the
    /// notice slot is claimable exactly once until the seam resets it.
    /// </summary>
    [Fact]
    public void DisableForTesting_ForcesSuppressionAndResetsNoticeGuard()
    {
        AgentSessionSuppression.DisableForTesting();

        try
        {
            Assert.True(AgentSessionSuppression.IsDisabled);
            Assert.True(AgentSessionSuppression.TryBeginSuppressionNotice());
            Assert.False(AgentSessionSuppression.TryBeginSuppressionNotice());

            // Re-forcing the seam restores the once-per-process notice slot.
            AgentSessionSuppression.DisableForTesting();
            Assert.True(AgentSessionSuppression.IsDisabled);
            Assert.True(AgentSessionSuppression.TryBeginSuppressionNotice());
        }
        finally
        {
            AgentSessionSuppression.ResetForTesting();
        }
    }

    /// <summary>
    /// Resetting the seam restores the once-per-process notice slot so notice-emission tests stay deterministic.
    /// </summary>
    [Fact]
    public void ResetForTesting_RestoresNoticeGuard()
    {
        AgentSessionSuppression.DisableForTesting();

        try
        {
            Assert.True(AgentSessionSuppression.TryBeginSuppressionNotice());
        }
        finally
        {
            AgentSessionSuppression.ResetForTesting();
        }

        Assert.True(AgentSessionSuppression.TryBeginSuppressionNotice());

        // Leave the shared static state clean for any following test.
        AgentSessionSuppression.ResetForTesting();
    }
}
