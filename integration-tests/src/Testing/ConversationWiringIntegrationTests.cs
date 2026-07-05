using AlleyCat.Body.Voice;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Provider;
using AlleyCat.Mind.AI.Tool;
using AlleyCat.Speech.Transcription;
using AlleyCat.TestFramework;
using Godot;
using Microsoft.Extensions.AI;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Testing;

/// <summary>
/// Regression coverage for focused player-to-NPC conversation wiring.
/// </summary>
[Headless]
public sealed class ConversationWiringIntegrationTests
{
    private const string ReferenceFemalePlayerScenePath =
        "res://assets/characters/templates/reference_female/reference_female_player.tscn";

    private const string ReferenceFemaleNpcScenePath =
        "res://assets/characters/templates/reference_female/reference_female_npc.tscn";

    /// <summary>
    /// The focused reference-female fixture keeps the player transcriber, player voice, NPC listener, mind voice, prompt, and tools wired.
    /// </summary>
    [Fact]
    public async Task ReferenceFemaleConversationFixture_TranscriptionSignal_ReachesNpcMindResponsePath()
    {
        SceneTree sceneTree = GetSceneTree();
        Node fixture = CreateConversationFixture();
        RecordingClientProvider clientProvider = new();

        AddTestNode(sceneTree, fixture);
        await WaitForFramesAsync(sceneTree, 16);

        try
        {
            Node player = fixture.GetNode("Actors/Player");
            Node npc = fixture.GetNode("Actors/NPC");
            PlayerVoice playerVoice = FindSingleDescendant<PlayerVoice>(player);
            Transcriber transcriber = FindSingleDescendant<Transcriber>(player);
            AgenticMind mind = FindSingleDescendant<AgenticMind>(npc);
            Voice npcVoice = Assert.IsAssignableFrom<Voice>(mind.Voice);

            Assert.Equal("player", playerVoice.Id);
            Assert.Same(transcriber, playerVoice.Transcriber);
            Assert.Equal("player", mind.PlayerVoiceId);
            Assert.True(mind.IsInGroup(IVoiceListener.GroupName));
            Assert.Contains(mind, sceneTree.GetNodesInGroup(IVoiceListener.GroupName).Cast<Node>());
            Assert.True(IsSameOrDescendant(npc, npcVoice));
            Assert.NotNull(mind.SystemInstruction);

            AgentTool tool = Assert.Single(mind.Tools);
            _ = Assert.IsType<SpeechTool>(tool, exactMatch: false);
            Assert.Equal("speak", tool.ToolName);
            Assert.False(string.IsNullOrWhiteSpace(tool.ToolDescription));

            mind.ClientProvider = clientProvider;
            mind.PostReplyListenCooldownSeconds = 0f;

            _ = transcriber.EmitSignal(Transcriber.SignalName.TranscriptionCompleted, "  hello reference friend  ");

            await WaitUntilAsync(sceneTree, () => clientProvider.Client is { RunCount: 1 }, maxFrames: 180);

            RecordingChatClient client = Assert.IsType<RecordingChatClient>(clientProvider.Client);
            Assert.Contains("- Speech from player: hello reference friend", Assert.Single(client.Prompts), StringComparison.Ordinal);
            Assert.Equal(["speak"], client.ToolNamesByRun);
        }
        finally
        {
            fixture.QueueFree();
            await WaitForFramesAsync(sceneTree, 2);
        }
    }

    private static Node CreateConversationFixture()
    {
        var fixture = new Node
        {
            Name = "ConversationWiringFixture",
        };
        var actors = new Node
        {
            Name = "Actors",
        };

        Node player = LoadPackedScene(ReferenceFemalePlayerScenePath).Instantiate();
        player.Name = "Player";
        Node npc = LoadPackedScene(ReferenceFemaleNpcScenePath).Instantiate();
        npc.Name = "NPC";

        fixture.AddChild(actors);
        actors.AddChild(player);
        actors.AddChild(npc);

        return fixture;
    }

    private static void AddTestNode(SceneTree sceneTree, Node node)
    {
        Node parent = sceneTree.CurrentScene ?? sceneTree.Root;
        parent.AddChild(node);
    }

    private static T FindSingleDescendant<T>(Node root)
        where T : Node
    {
        List<T> matches = [];
        CollectDescendants(root, matches);
        return Assert.Single(matches);
    }

    private static void CollectDescendants<T>(Node node, ICollection<T> matches)
        where T : Node
    {
        if (node is T match)
        {
            matches.Add(match);
        }

        foreach (Node child in node.GetChildren())
        {
            CollectDescendants(child, matches);
        }
    }

    private static bool IsSameOrDescendant(Node root, Node node)
    {
        for (Node? current = node; current is not null; current = current.GetParent())
        {
            if (ReferenceEquals(current, root))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task WaitUntilAsync(SceneTree sceneTree, Func<bool> predicate, int maxFrames)
    {
        for (int frame = 0; frame < maxFrames; frame++)
        {
            if (predicate())
            {
                return;
            }

            await WaitForNextFrameAsync(sceneTree);
        }

        Assert.True(predicate(), $"Condition was not met within {maxFrames} frames.");
    }

    private sealed class RecordingClientProvider : ClientProvider
    {
        public RecordingChatClient? Client
        {
            get; private set;
        }

        public override IChatClient CreateChatClient()
        {
            Client = new RecordingChatClient();
            return Client;
        }
    }

    private sealed class RecordingChatClient : IChatClient
    {
        public int RunCount
        {
            get; private set;
        }

        public List<string> Prompts { get; } = [];

        public List<string> ToolNamesByRun { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RunCount++;
            Prompts.Add(messages.Last().Text);
            ToolNamesByRun.AddRange(options?.Tools?.Select(tool => tool.Name) ?? []);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (ChatResponseUpdate update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
