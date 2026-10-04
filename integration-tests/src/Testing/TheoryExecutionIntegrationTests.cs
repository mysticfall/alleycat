using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Testing;

/// <summary>
/// Runtime-side contract coverage proving the integration runner discovers and executes data-driven
/// <see cref="TheoryAttribute"/> tests inside a Godot session (specs/testing/001-test-framework, TR1).
/// </summary>
[Headless]
public sealed class TheoryExecutionIntegrationTests
{
    /// <summary>Rows supplied through <see cref="MemberDataAttribute"/>, including a Godot value type.</summary>
    public static TheoryData<string, Vector3> MemberRows => new()
    {
        { "unit-x", Vector3.Right },
        { "unit-y", Vector3.Up },
    };

    /// <summary>Verifies inline rows reach the test body with their bound arguments.</summary>
    [Theory]
    [InlineData(1, 2, 3)]
    [InlineData(-4, 4, 0)]
    public void InlineDataRows_ReceiveBoundArguments(int left, int right, int expectedSum)
        => Assert.Equal(expectedSum, left + right);

    /// <summary>Verifies member-data rows reach the test body inside the running engine.</summary>
    [Theory]
    [MemberData(nameof(MemberRows))]
    public void MemberDataRows_ReceiveBoundArguments(string label, Vector3 axis)
    {
        Assert.StartsWith("unit-", label, StringComparison.Ordinal);
        Assert.Equal(1.0f, axis.Length(), 5);
        Assert.NotNull(Engine.GetMainLoop());
    }
}
