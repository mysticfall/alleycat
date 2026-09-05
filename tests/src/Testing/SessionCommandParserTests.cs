using System.Text.Json;
using AlleyCat.Testing;
using Xunit;

namespace AlleyCat.Tests.Testing;

/// <summary>
/// Tests the pure host-to-runtime session command boundary used before Godot-thread dispatch.
/// </summary>
public sealed class SessionCommandParserTests
{
    /// <summary>
    /// Gets command payloads with one or more required fields absent.
    /// </summary>
    public static TheoryData<string> IncompleteCommands =>
    [
        "{}",
        JsonSerializer.Serialize(new { Kind = "run", RequestId = "request", Type = "Fixture", Method = "Test" }),
        JsonSerializer.Serialize(new { Version = 1, RequestId = "request", Type = "Fixture", Method = "Test" }),
        JsonSerializer.Serialize(new { Version = 1, Kind = "run", Type = "Fixture", Method = "Test" }),
        JsonSerializer.Serialize(new { Version = 1, Kind = "run", RequestId = "request", Method = "Test" }),
        JsonSerializer.Serialize(new { Version = 1, Kind = "run", RequestId = "request", Type = "Fixture" }),
        JsonSerializer.Serialize(new { Kind = "shutdown", RequestId = "request" }),
        JsonSerializer.Serialize(new { Version = 1, RequestId = "request" }),
        JsonSerializer.Serialize(new { Version = 1, Kind = "shutdown" }),
    ];

    /// <summary>
    /// Gets syntactically valid payloads that are nevertheless unsupported command shapes.
    /// </summary>
    public static TheoryData<string> WrongShapeAndUnknownCommands =>
    [
        JsonSerializer.Serialize(Array.Empty<string>()),
        JsonSerializer.Serialize(new { Version = 1, Kind = "unknown", RequestId = "request" }),
    ];

    /// <summary>
    /// Gets complete commands accepted by the session protocol.
    /// </summary>
    public static TheoryData<string> ValidCommands =>
    [
        JsonSerializer.Serialize(new { Version = 1, Kind = "run", RequestId = "request", Type = "Fixture", Method = "Test" }),
        JsonSerializer.Serialize(new { Version = 1, Kind = "shutdown", RequestId = "request" }),
    ];

    /// <summary>
    /// Ensures every incomplete command is rejected before the runner can log-and-skip it rather than dispatching it
    /// or emitting a correlated result.
    /// </summary>
    [Theory]
    [MemberData(nameof(IncompleteCommands))]
    public void TryParse_RejectsIncompleteCommands(string line)
    {
        bool parsed = SessionCommandParser.TryParse(line, out SessionCommand? command, out string? error);

        Assert.False(parsed);
        Assert.Null(command);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    /// <summary>
    /// Ensures unknown or syntactically valid non-object inputs are rejected with the same log-and-skip contract.
    /// </summary>
    [Theory]
    [MemberData(nameof(WrongShapeAndUnknownCommands))]
    public void TryParse_RejectsWrongShapeAndUnknownKind(string line)
    {
        bool parsed = SessionCommandParser.TryParse(line, out SessionCommand? command, out string? error);

        Assert.False(parsed);
        Assert.Null(command);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    /// <summary>
    /// Ensures valid run and shutdown commands remain dispatchable.
    /// </summary>
    [Theory]
    [MemberData(nameof(ValidCommands))]
    public void TryParse_AcceptsValidCommands(string line)
    {
        bool parsed = SessionCommandParser.TryParse(line, out SessionCommand? command, out string? error);

        Assert.True(parsed);
        Assert.NotNull(command);
        Assert.Null(error);
    }
}
