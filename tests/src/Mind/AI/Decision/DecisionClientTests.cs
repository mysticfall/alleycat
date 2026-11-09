using System.Net;
using System.Text.Json;
using AlleyCat.Mind.AI.Decision;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlleyCat.Tests.Mind.AI.Decision;

/// <summary>Offline public-boundary wire fixtures.</summary>
public partial class DecisionClientTests
{
    private static DecisionOptions Options(string kind = "OpenRouter", double timeout = 5) => new()
    {
        APIKind = kind,
        Host = kind == "OpenRouter" ? "https://example.invalid/api/alpha" : "https://example.invalid/v1/",
        Model = "configured",
        ApiKey = "fixture-key",
        Timeout = timeout
    };

    private static DecisionRequest Request(object evidence) => new(evidence,
    [
        new BooleanQuestion("bug", "Is it broken?"),
        new ChoiceQuestion("team", "Who owns this?", [new("frontend", "Rendering"), new("payments", "Billing")]),
        new ScoreQuestion("urgency", "How urgent?", ["Next release", "This week", "Now"])
    ]);

    private const string RouterResponse = /*lang=json,strict*/ """
        {"model":"resolved","id":"request-1","provider":"TypeSafe","usage":{"input_tokens":476,"output_tokens":70,"cost":0.00002},
         "answers":{"urgency":{"type":"score","score":1.99,"legend":{"0":"Next release","1":"This week","2":"Now"},"probabilities":{"0":0,"1":0.01,"2":0.99}},
         "bug":{"type":"noul","noul":0.96},"team":{"type":"choice","choice":"payments","confidence":0.75,"probabilities":{"payments":0.84,"frontend":0.16}}}}
        """;
    private const string OpenAIResponse = /*lang=json,strict*/ """
        {"model":"resolved","answers":[{"name":"team","type":"choice","choice":"payments","probabilities":[{"value":"payments","probability":0.84},{"value":"frontend","probability":0.16}]},
         {"name":"bug","type":"predicate","probability":0.96},
         {"name":"urgency","type":"score","score":1.99,"probabilities":[{"value":2,"label":"Now","probability":0.99},{"value":0,"label":"Next release","probability":0},{"value":1,"label":"This week","probability":0.01}]}]}
        """;

    /// <summary>Independent examples from the documented dialects preserve evidence and typed identities.</summary>
    [Theory]
    [InlineData("OpenRouter", false)]
    [InlineData("OpenRouter", true)]
    [InlineData("OpenAI", false)]
    [InlineData("OpenAI", true)]
    public async Task CompleteDocumentedDialects(string kind, bool json)
    {
        using var evidence = JsonDocument.Parse("{\"ticket\":\"broken checkout\"}");
        DecisionRequest request = Request(json ? evidence.RootElement : "broken checkout");
        using HttpClient http = new(new Handler(async (message, token) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal(kind == "OpenRouter" ? "/api/alpha/decisions" : "/v1/decisions", message.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", message.Headers.Authorization!.Scheme);
            using var payload = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            JsonElement root = payload.RootElement;
            Assert.Equal("configured", root.GetProperty("model").GetString());
            if (kind == "OpenRouter")
            {
                Assert.Equal(json ? JsonValueKind.Object : JsonValueKind.String, root.GetProperty("state").ValueKind);
                JsonElement questions = root.GetProperty("questions");
                Assert.Equal(["bug", "team", "urgency"], questions.EnumerateObject().Select(p => p.Name));
                Assert.Equal("noul", questions.GetProperty("bug").GetProperty("type").GetString());
                Assert.False(questions.GetProperty("bug").TryGetProperty("criteria", out _));
                Assert.Equal(["frontend", "payments"], questions.GetProperty("team").GetProperty("criteria").EnumerateObject().Select(p => p.Name));
                Assert.Equal("Billing", questions.GetProperty("team").GetProperty("criteria").GetProperty("payments").GetString());
                Assert.Equal(["Next release", "This week", "Now"], questions.GetProperty("urgency").GetProperty("criteria").EnumerateArray().Select(p => p.GetString()));
            }
            else
            {
                JsonElement input = root.GetProperty("input");
                if (json)
                {
                    Assert.Equal("user", input[0].GetProperty("role").GetString());
                    Assert.Equal("input_json", input[0].GetProperty("content")[0].GetProperty("type").GetString());
                    Assert.Equal("broken checkout", input[0].GetProperty("content")[0].GetProperty("data").GetProperty("ticket").GetString());
                }
                else
                {
                    Assert.Equal("broken checkout", input.GetString());
                }

                JsonElement questions = root.GetProperty("questions");
                Assert.Equal(["bug", "team", "urgency"], questions.EnumerateArray().Select(p => p.GetProperty("name").GetString()));
                Assert.Equal("predicate", questions[0].GetProperty("type").GetString());
                Assert.Equal("payments", questions[1].GetProperty("choices")[1].GetProperty("value").GetString());
                Assert.Equal("Billing", questions[1].GetProperty("choices")[1].GetProperty("description").GetString());
                Assert.Equal("Now", questions[2].GetProperty("levels")[2].GetProperty("label").GetString());
            }
            return Response(kind == "OpenRouter" ? RouterResponse : OpenAIResponse);
        }))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        DecisionClient client = new(http, Options(kind), NullLogger<DecisionClient>.Instance);
        DecisionResult result = await client.EvaluateAsync(request);
        Assert.Equal("resolved", result.Model);
        Assert.Equal(["bug", "team", "urgency"], result.Answers.Select(answer => answer.Name));
        Assert.Equal(0.96, Assert.IsType<BooleanAnswer>(result.ByName["bug"]).Probability);
        ChoiceAnswer choice = Assert.IsType<ChoiceAnswer>(result.ByName["team"]);
        Assert.Equal("payments", choice.Value);
        Assert.Equal(["frontend", "payments"], choice.Probabilities!.Keys);
        Assert.Equal(1.99, Assert.IsType<ScoreAnswer>(result.ByName["urgency"]).Score);
        if (kind == "OpenRouter")
        {
            Assert.Equal("request-1", result.ID);
            Assert.Equal("TypeSafe", result.Provider);
            Assert.Equal(476, result.Usage!.InputTokens);
            Assert.Equal(0.00002, result.Cost);
        }
        else
        {
            Assert.Null(result.ID);
            Assert.Null(result.Usage);
            Assert.Null(choice.Confidence);
        }
    }

    /// <summary>Host prefix and optional trailing slash are preserved with exactly one operation segment.</summary>
    [Theory]
    [InlineData("OpenRouter", "https://example.invalid/api/alpha", "/api/alpha/decisions")]
    [InlineData("OpenRouter", "https://example.invalid/api/alpha/", "/api/alpha/decisions")]
    [InlineData("OpenAI", "https://example.invalid/v1", "/v1/decisions")]
    [InlineData("OpenAI", "https://example.invalid/v1/", "/v1/decisions")]
    [InlineData("OpenAI", "https://example.invalid/proxy/v1/", "/proxy/v1/decisions")]
    public async Task ComposesBasePaths(string kind, string host, string expectedPath)
    {
        using HttpClient http = new(new Handler((message, _) =>
        {
            Assert.Equal(expectedPath, message.RequestUri!.AbsolutePath);
            return Task.FromResult(Response(kind == "OpenRouter" ? RouterResponse : OpenAIResponse));
        }))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        DecisionOptions options = new()
        {
            APIKind = kind,
            Host = host,
            Model = "configured",
            ApiKey = "fixture-key",
            Timeout = 5
        };
        _ = await new DecisionClient(http, options, NullLogger<DecisionClient>.Instance).EvaluateAsync(Request("evidence"));
    }

    private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(body)
        };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
