using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.Observation;
using AlleyCat.Vision;
using Godot;
using Microsoft.Extensions.AI;
using Xunit;
using AgentObservation = AlleyCat.Mind.Observation.Observation;

namespace AlleyCat.Tests.Mind.AI;

/// <summary>
/// Tests the runtime-authored synthetic scene-update delivery's pure selection and construction contracts
/// (AI-002 TR-18, TR-21, TR-19) without a network backend or Godot runtime.
/// </summary>
public sealed class SyntheticSceneUpdateExchangeTests
{
    private const string OwnerFullId = "char:rin";

    /// <summary>Synthetic call IDs use a distinct namespace, separable from provider call IDs (AI-002 TR-18).</summary>
    [Fact]
    public void FormatCallId_UsesTheDistinctSyntheticNamespace()
    {
        Assert.Equal("runtime-scene-update-1", SyntheticSceneUpdateExchange.FormatCallId(1));
        Assert.Equal("runtime-scene-update-12", SyntheticSceneUpdateExchange.FormatCallId(12));
        Assert.StartsWith(SyntheticSceneUpdateExchange.SyntheticCallIDPrefix, SyntheticSceneUpdateExchange.FormatCallId(1), StringComparison.Ordinal);
    }

    /// <summary>Only entries beyond the delivered watermark are pending (AI-002 TR-18/TR-19).</summary>
    [Fact]
    public void SelectPending_ReturnsOnlyEntriesBeyondTheDeliveredWatermark()
    {
        AcceptedObservationEntry[] timeline =
        [
            Entry(1, Speech("char:other", "first")),
            Entry(2, Speech("char:other", "a middle line")),
            Entry(3, Speech("char:other", "second")),
        ];

        AcceptedObservationEntry[] pending =
        [
            .. SyntheticSceneUpdateExchange.SelectPending(timeline, deliveredSequenceID: 1, new FakeCharacter()),
        ];

        Assert.Equal([2, 3], pending.Select(static entry => entry.SequenceID));
        Assert.Empty(SyntheticSceneUpdateExchange.SelectPending(timeline, 3, new FakeCharacter()));
        Assert.Empty(SyntheticSceneUpdateExchange.SelectPending([], 0, new FakeCharacter()));
    }

    /// <summary>
    /// The owning character's persisted self-speech observation is never delivered as a scene update: it reaches the
    /// model through its own retained speak call (AI-002 TR-21).
    /// </summary>
    [Fact]
    public void SelectPending_ExcludesTheOwnersOwnSelfSpeech()
    {
        AcceptedObservationEntry[] timeline =
        [
            Entry(1, Speech("char:other", "their line")),
            Entry(2, Speech(OwnerFullId, "my own line")),
            Entry(3, Speech(null, "unknown speaker")),
        ];

        AcceptedObservationEntry[] pending =
        [
            .. SyntheticSceneUpdateExchange.SelectPending(timeline, 0, new FakeCharacter()),
        ];

        Assert.Equal([1, 3], pending.Select(static entry => entry.SequenceID));
    }

    /// <summary>A materialisation with nothing pending creates no synthetic exchange (AI-002 TR-18).</summary>
    [Fact]
    public async Task BuildAsync_WithNothingPending_CreatesNoExchange()
    {
        IReadOnlyList<ChatMessage> exchange = await SyntheticSceneUpdateExchange.BuildAsync(
            [Entry(1, Speech("char:other", "delivered already"))],
            deliveredSequenceID: 1,
            new FakeCharacter(),
            SyntheticSceneUpdateExchange.FormatCallId(1));

        Assert.Empty(exchange);
    }

    /// <summary>
    /// The synthetic exchange is one assistant message carrying a single delivery-tool call plus its tool result
    /// rendering the observations in observation-owned canonical text, joined with exactly one newline (AI-002
    /// TR-18, AI-003 TR-8).
    /// </summary>
    [Fact]
    public async Task BuildAsync_RendersOneCallAndCanonicalObservationTextPerLine()
    {
        AcceptedObservationEntry[] timeline =
        [
            Entry(1, Speech("char:other", "hello there")),
            Entry(2, Speech("char:other", "second line")),
        ];

        const string callId = "runtime-scene-update-1";
        IReadOnlyList<ChatMessage> exchange = await SyntheticSceneUpdateExchange.BuildAsync(
            timeline,
            deliveredSequenceID: 0,
            new FakeCharacter(),
            callId);

        Assert.Equal(2, exchange.Count);
        Assert.Equal(ChatRole.Assistant, exchange[0].Role);
        FunctionCallContent call = Assert.IsType<FunctionCallContent>(Assert.Single(exchange[0].Contents));
        Assert.Equal(callId, call.CallId);
        Assert.Equal(SyntheticSceneUpdateExchange.DeliveryToolName, call.Name);
        Assert.Equal(ChatRole.Tool, exchange[1].Role);
        FunctionResultContent result = Assert.IsType<FunctionResultContent>(Assert.Single(exchange[1].Contents));
        Assert.Equal(callId, result.CallId);
        string[] lines = Assert.IsType<string>(result.Result).Split('\n');
        Assert.Equal("Heard `char:other` say: hello there", lines[0]);
        Assert.Equal("Heard `char:other` say: second line", lines[1]);
        Assert.Equal(2, lines.Length);
    }

    private static ObservedSpeech Speech(string? actorId, string content)
        => new(actorId, content);

    private static AcceptedObservationEntry Entry(long sequenceID, AgentObservation payload)
        => new(
            sequenceID,
            ObservedAt: 10d,
            payload,
            new ObservationSchedulingMetadata(Importance: 1f, RequiresFreshTurn: false),
            IsRetained: true);

    private sealed class FakeCharacter : ICharacter
    {
        public string Id { get; set; } = "rin";

        public string Type { get; set; } = "char";

        public string FullId => OwnerFullId;

        public IReadOnlyList<IComponent> Components { get; } = [];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public Transform3D GlobalTransform { get; set; } = Transform3D.Identity;
    }
}
