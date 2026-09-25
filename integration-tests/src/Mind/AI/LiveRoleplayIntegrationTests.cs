using AlleyCat.Core.Content;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.IntegrationTests.Support.AI;
using AlleyCat.Mind.AI.Lore;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Scene;
using AlleyCat.Templating;
using AlleyCat.TestFramework;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation.Quality;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AlleyCat.IntegrationTests.Mind.AI;

/// <summary>
/// Opt-in live LLM roleplay fixture: Vadim answers a narrow records question about Ally strictly from his own
/// perspective lore, and an independent judge client scores the answer's groundedness.
/// </summary>
/// <remarks>
/// <para>
/// Deterministic pre-network assertions prove the real prompt pipeline (prompt stack, pseudo-XML writer, Fluid
/// compiler, character lore section) carries the committed perspective facts and resolved identities. Deterministic
/// post-response assertions constrain the reply shape. The live target call and judge verdict complement those
/// assertions; they never replace them.
/// </para>
/// <para>
/// Excluded from discovery and execution unless the <c>--live-llm</c> flag is supplied.
/// </para>
/// </remarks>
[Headless]
[LiveLlm]
public sealed class LiveRoleplayIntegrationTests
{
    private const string ObserverFullId = "char:vadim";

    private const string PlayerQuestion =
        "What does Ally's record say about prior breaches and flagged associations, and who makes the final determination?";

    /// <summary>
    /// Vadim answers about Ally from his available record; the judge scores groundedness at threshold 5 because the
    /// narrow question requires every fact in the record and no invention (the library's built-in
    /// greater-than-or-equal-to-4 interpretation is insufficient here).
    /// </summary>
    [Fact]
    public async Task VadimAnswersAboutAllyFromHisAvailableRecord()
    {
        ServiceCollection services = new();
        PseudoXmlPromptWriter writer = new();
        FluidTemplateCompiler compiler = new();
        writer.RegisterServices(services);
        compiler.RegisterServices(services);
        _ = services
            .AddSingleton<ILoreQueryService, MarkdownLoreQueryService>()
            .AddSingleton<ILorePromptFormatter, MarkdownLorePromptFormatter>();

        using ServiceProvider provider = services.BuildServiceProvider();
        PromptOwnerCharacter vadim = new("vadim");
        PromptOwnerCharacter ally = new("ally");
        SceneContext scene = new([vadim, ally], ContentContext.Default);
        PromptSectionBuildContext buildContext = new(provider, scene, vadim);

        CharacterLorePromptSection loreSection = new()
        {
            Name = "Character Records"
        };
        string groundingContext = await loreSection.GetContentAsync(buildContext);

        PromptStack stack = new()
        {
            Sections =
            [
                new TextPromptSection
                {
                    Name = "System Instructions",
                    Text = """
                        You are {{ observer_full_id }}, an officer of the Charter Office of Compliance. You are calm,
                        direct, and unhurried. Answer the player's question briefly and in character, using only the
                        knowledge supplied in your records. Do not invent facts that are not present in those records.
                        """,
                },
                loreSection,
            ],
        };

        ITemplate template = await stack.CompileAsync(buildContext);
        string systemPrompt = await template.RenderAsync(
            new Dictionary<string, object?> { ["observer_full_id"] = ObserverFullId });

        // Deterministic pre-network assertions: the real rendered prompt carries the committed perspective facts
        // for both scene identities, the writer's section structure, and the resolved template identity.
        Assert.Contains("<System Instructions>", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("<Character Records>", systemPrompt, StringComparison.Ordinal);
        Assert.Contains(ObserverFullId, systemPrompt, StringComparison.Ordinal);
        Assert.Contains("char:ally", systemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", systemPrompt, StringComparison.Ordinal);
        Assert.Contains(
            "steady employment, no prior breaches, no flagged associations",
            systemPrompt,
            StringComparison.Ordinal);
        Assert.Contains("My involvement ends at the recommendation", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("The determination belongs to the Office", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("I am calm, direct, and unhurried", groundingContext, StringComparison.Ordinal);

        // The running Game's merged configuration is the only authorised credential boundary: the factory fails
        // with its actionable, secret-free message when the AITest section is missing or invalid.
        LiveLLMClientPair clients = LiveLLMClientFactory.CreateLiveClients();

        List<ChatMessage> messages =
        [
            new (ChatRole.System, systemPrompt),
            new (ChatRole.User, PlayerQuestion),
        ];

        LiveLLMEvaluationOutcome outcome = await LiveLLMEvaluation.EvaluateAsync(
            messages,
            clients,
            new GroundednessEvaluator(),
            new GroundednessEvaluatorContext(groundingContext),
            threshold: 5);

        // Deterministic post-response assertions: a spoken answer, no tool attempts, and no leaked identifiers.
        Assert.False(string.IsNullOrWhiteSpace(outcome.TargetResponse.Text));
        Assert.DoesNotContain(
            outcome.TargetResponse.Messages.SelectMany(static message => message.Contents),
            static content => content is FunctionCallContent);
        Assert.DoesNotContain("char:", outcome.TargetResponse.Text, StringComparison.Ordinal);
    }
}
