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
        Host = kind switch
        {
            "OpenRouter" => "https://example.invalid/api/alpha",
            "SystemOne" => "http://localhost:8081/v1",
            _ => "https://example.invalid/v1/"
        },
        Model = kind == "SystemOne" ? null : "configured",
        ApiKey = kind == "SystemOne" ? null : "fixture-key",
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

    // Real bytes captured from the local kev-decision-api service (llama-server /v1/systemone).
    // Retained evidence: game/temp/local-kev4b/{request.packed.json,response.json} (six-question scored pass).
    private const string SystemOneState = """
        냉장고에는 사과 3개와 배 2개가 들어 있다. 서랍에는 귤 5개가 들어 있다.
        우진은 민지보다 키가 크다. 민지는 하진보다 키가 크다.
        가방은 파란색이고 우산은 빨간색이다. 모자는 파란색도 빨간색도 아니다.
        민지의 컵은 초록색이다.
        English control facts: the meeting room has 4 chairs and 1 table.
        """;

    private const string SystemOneRetainedResponse = /*lang=json,strict*/ """
        {
          "model": "kev-4b-q4km",
          "answers": {
            "apples": {
              "type": "choice",
              "choice": "3",
              "probabilities": {
                "2": 0.0035579587128065243,
                "3": 0.9929571485323149,
                "5": 0.003484892754878686
              },
              "confidence": 0.9894357227984721
            },
            "mandarins": {
              "type": "choice",
              "choice": "5",
              "probabilities": {
                "2": 0.016056211572394005,
                "3": 0.014469933286814366,
                "5": 0.9694738551407917
              },
              "confidence": 0.9542107827111873
            },
            "shortest": {
              "type": "choice",
              "choice": "하진",
              "probabilities": {
                "우진": 0.016330148863556108,
                "민지": 0.22456488858403575,
                "하진": 0.7591049625524081
              },
              "confidence": 0.6386574438286121
            },
            "hat_colour": {
              "type": "choice",
              "choice": "yellow",
              "probabilities": {
                "blue": 0.34012573201502333,
                "red": 0.04361080115362446,
                "yellow": 0.6162634668313522
              },
              "confidence": 0.42439520024702837
            },
            "cup_colour": {
              "type": "choice",
              "choice": "green",
              "probabilities": {
                "green": 0.9965520481326814,
                "blue": 0.0024797009929271186,
                "red": 0.0009682508743912982
              },
              "confidence": 0.9948280721990219
            },
            "control_chairs": {
              "type": "choice",
              "choice": "4",
              "probabilities": {
                "1": 0.021326155299841137,
                "4": 0.9063795208871748,
                "5": 0.07229432381298419
              },
              "confidence": 0.8595692813307622
            }
          },
          "usage": {
            "input_tokens": 817,
            "output_tokens": 0
          }
        }
        """;

    // Real bytes from the live service probe (game/temp/decision-systemone-01/probe/mixed.json):
    // one boolean, one choice and one score answer in a single captured body.
    private const string SystemOneMixedResponse = /*lang=json,strict*/ """
        {"model":"kev-4b-q4km","answers":{"window":{"type":"choice","choice":"closed","probabilities":{"open":0.0013162897765396885,"closed":0.9986837102234603},"confidence":0.9973674204469205},"lamp_on":{"type":"noul","noul":0.9949448892249979},"certainty":{"type":"score","score":1.8932948576380748,"legend":{"0":"Not stated","1":"Implied","2":"Explicitly stated"},"probabilities":{"0":0.039666549488064404,"1":0.027372043385796657,"2":0.9329614071261391},"confidence":0.8399422864571118}},"usage":{"input_tokens":109,"output_tokens":0}}
        """;

    private static DecisionRequest RetainedSystemOneRequest() => new(SystemOneState,
    [
        new ChoiceQuestion("apples", "According to the passage, how many apples are in the fridge?",
            [new("2", "Two apples"), new("3", "Three apples"), new("5", "Five apples")]),
        new ChoiceQuestion("mandarins", "According to the passage, how many mandarins are in the drawer?",
            [new("2", "Two mandarins"), new("3", "Three mandarins"), new("5", "Five mandarins")]),
        new ChoiceQuestion("shortest", "According to the passage, who is the shortest of the three people?",
            [new("우진", "Ujin"), new("민지", "Minji"), new("하진", "Hajin")]),
        new ChoiceQuestion("hat_colour", "According to the passage, what colour is the hat?",
            [new("blue", "Blue"), new("red", "Red"), new("yellow", "Yellow")]),
        new ChoiceQuestion("cup_colour", "According to the passage, what colour is Minji's cup?",
            [new("green", "Green"), new("blue", "Blue"), new("red", "Red")]),
        new ChoiceQuestion("control_chairs", "According to the passage, how many chairs are in the meeting room?",
            [new("1", "One chair"), new("4", "Four chairs"), new("5", "Five chairs")])
    ]);

    /// <summary>SystemOne targets /v1/systemone without credentials and parses the retained real service envelope.</summary>
    [Fact]
    public async Task SystemOneParsesRetainedServiceResponse()
    {
        using HttpClient http = new(new Handler(async (message, token) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal("http://localhost:8081/v1/systemone", message.RequestUri!.AbsoluteUri);
            Assert.Null(message.Headers.Authorization);
            using var payload = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            JsonElement root = payload.RootElement;
            Assert.Equal(string.Empty, root.GetProperty("model").GetString());
            Assert.Equal(SystemOneState, root.GetProperty("state").GetString());
            JsonElement questions = root.GetProperty("questions");
            Assert.Equal(["apples", "mandarins", "shortest", "hat_colour", "cup_colour", "control_chairs"],
                questions.EnumerateObject().Select(property => property.Name));
            Assert.Equal("choice", questions.GetProperty("apples").GetProperty("type").GetString());
            Assert.Equal("Three apples", questions.GetProperty("apples").GetProperty("criteria").GetProperty("3").GetString());
            Assert.Equal("Hajin", questions.GetProperty("shortest").GetProperty("criteria").GetProperty("하진").GetString());
            return Response(SystemOneRetainedResponse);
        }))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        DecisionResult result = await new DecisionClient(http, Options("SystemOne"), NullLogger<DecisionClient>.Instance)
            .EvaluateAsync(RetainedSystemOneRequest());
        Assert.Equal("kev-4b-q4km", result.Model);
        Assert.Null(result.ID);
        Assert.Null(result.Provider);
        Assert.Null(result.Cost);
        Assert.Equal(817L, result.Usage!.InputTokens);
        Assert.Equal(0L, result.Usage.OutputTokens);
        Assert.Equal(["apples", "mandarins", "shortest", "hat_colour", "cup_colour", "control_chairs"],
            result.Answers.Select(answer => answer.Name));
        ChoiceAnswer apples = Assert.IsType<ChoiceAnswer>(result.ByName["apples"]);
        Assert.Equal("3", apples.Value);
        Assert.Equal(0.9894357227984721, apples.Confidence);
        Assert.Equal(["2", "3", "5"], apples.Probabilities!.Keys);
        Assert.Equal(0.9929571485323149, apples.Probabilities!["3"]);
        ChoiceAnswer shortest = Assert.IsType<ChoiceAnswer>(result.ByName["shortest"]);
        Assert.Equal("하진", shortest.Value);
        Assert.Equal(["우진", "민지", "하진"], shortest.Probabilities!.Keys);
        ChoiceAnswer cup = Assert.IsType<ChoiceAnswer>(result.ByName["cup_colour"]);
        Assert.Equal("green", cup.Value);
        Assert.Equal(["green", "blue", "red"], cup.Probabilities!.Keys);
        ChoiceAnswer control = Assert.IsType<ChoiceAnswer>(result.ByName["control_chairs"]);
        Assert.Equal("4", control.Value);
        Assert.Equal(0.9063795208871748, control.Probabilities!["4"]);
    }

    /// <summary>SystemOne boolean and score answers follow the map dialect, including real score legends.</summary>
    [Fact]
    public async Task SystemOneBooleanAndScoreAnswersMatchCapturedServiceBytes()
    {
        DecisionRequest request = new("The lamp is switched on. The window is closed.",
        [
            new ChoiceQuestion("window", "According to the passage, what state is the window in?",
                [new("open", "Open"), new("closed", "Closed")]),
            new BooleanQuestion("lamp_on", "According to the passage, is the lamp switched on?"),
            new ScoreQuestion("certainty", "According to the passage, how certain is it that the lamp is on?",
                ["Not stated", "Implied", "Explicitly stated"])
        ]);
        using HttpClient http = new(new Handler((_, _) => Task.FromResult(Response(SystemOneMixedResponse))))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        DecisionResult result = await new DecisionClient(http, Options("SystemOne"), NullLogger<DecisionClient>.Instance)
            .EvaluateAsync(request);
        Assert.Equal(0.9949448892249979, Assert.IsType<BooleanAnswer>(result.ByName["lamp_on"]).Probability);
        Assert.Null(Assert.IsType<BooleanAnswer>(result.ByName["lamp_on"]).Confidence);
        ChoiceAnswer window = Assert.IsType<ChoiceAnswer>(result.ByName["window"]);
        Assert.Equal("closed", window.Value);
        Assert.Equal(["open", "closed"], window.Probabilities!.Keys);
        ScoreAnswer score = Assert.IsType<ScoreAnswer>(result.ByName["certainty"]);
        Assert.Equal(1.8932948576380748, score.Score);
        Assert.Equal(0.8399422864571118, score.Confidence);
        Assert.Equal([0.039666549488064404, 0.027372043385796657, 0.9329614071261391], score.Probabilities!);
    }

    /// <summary>SystemOne never sends credentials, even when one is configured; hosted kinds keep the bearer header.</summary>
    [Theory]
    [InlineData("SystemOne", false)]
    [InlineData("OpenRouter", true)]
    public async Task CredentialHeaderFollowsDialect(string kind, bool sendsAuthorization)
    {
        using HttpClient http = new(new Handler((message, _) =>
        {
            Assert.Equal(sendsAuthorization ? "Bearer" : null, message.Headers.Authorization?.Scheme);
            return Task.FromResult(Response(kind == "OpenAI" ? OpenAIResponse : RouterResponse));
        }))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        DecisionOptions options = new()
        {
            APIKind = kind,
            Host = kind == "SystemOne" ? "http://localhost:8081/v1" : "https://example.invalid/api/alpha",
            Model = "configured",
            ApiKey = "fixture-key",
            Timeout = 5
        };
        _ = await new DecisionClient(http, options, NullLogger<DecisionClient>.Instance).EvaluateAsync(Request("evidence"));
    }

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
                Assert.Equal(["Next release", "This week", "Now"], questions.GetProperty("urgency").GetProperty("criteria").EnumerateArray().Select(v => v.GetString()));
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
                Assert.Equal(["bug", "team", "urgency"], questions.EnumerateArray().Select(v => v.GetProperty("name").GetString()));
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

    /// <summary>
    /// Boolean pole guidance rides the dialect's documented criteria shape: the noul criteria map on the
    /// map-based dialects, and instructions-appended plain text on the OpenAI dialect — nothing silently
    /// discarded. Without poles, neither dialect emits any criteria field.
    /// </summary>
    /// <param name="kind">Dialect under test.</param>
    [Theory]
    [InlineData("OpenRouter")]
    [InlineData("OpenAI")]
    public async Task BooleanCriteria_RenderPerDialectWithoutSilentLoss(string kind)
    {
        JsonDocument? captured = null;
        DecisionRequest request = new(/*lang=json,strict*/ "{\"ticket\":\"broken checkout\"}",
        [
            new BooleanQuestion(
                "bug",
                "Is it broken?",
                CriteriaTrue: "The report describes broken behaviour.",
                CriteriaFalse: "The report asks a question."),
        ]);
        string response = kind == "OpenRouter"
            ? /*lang=json,strict*/ """{"model":"resolved","answers":{"bug":{"type":"noul","noul":0.9}}}"""
            : /*lang=json,strict*/ """{"model":"resolved","answers":[{"name":"bug","type":"predicate","probability":0.9}]}""";
        using HttpClient http = new(new Handler(async (message, token) =>
        {
            captured = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            return Response(response);
        }))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        DecisionClient client = new(http, Options(kind), NullLogger<DecisionClient>.Instance);

        _ = await client.EvaluateAsync(request);

        JsonElement bug = kind == "OpenRouter"
            ? captured!.RootElement.GetProperty("questions").GetProperty("bug")
            : captured!.RootElement.GetProperty("questions")[0];
        if (kind == "OpenRouter")
        {
            Assert.Equal("Is it broken?", bug.GetProperty("instructions").GetString());
            Assert.Equal(
                "The report describes broken behaviour.",
                bug.GetProperty("criteria").GetProperty("true").GetString());
            Assert.Equal(
                "The report asks a question.",
                bug.GetProperty("criteria").GetProperty("false").GetString());
        }
        else
        {
            Assert.Equal(
                "Is it broken? Answer true when: The report describes broken behaviour. "
                    + "Answer false when: The report asks a question.",
                bug.GetProperty("instructions").GetString());
            _ = Assert.Throws<KeyNotFoundException>(() => bug.GetProperty("criteria"));
        }
    }

    /// <summary>A boolean question without poles emits no criteria field on either dialect.</summary>
    [Theory]
    [InlineData("OpenRouter")]
    [InlineData("OpenAI")]
    public async Task BooleanWithoutCriteria_EmitsNoCriteriaField(string kind)
    {
        JsonDocument? captured = null;
        DecisionRequest request = new(/*lang=json,strict*/ "{\"ticket\":\"broken checkout\"}", [new BooleanQuestion("bug", "Is it broken?")]);
        string response = kind == "OpenRouter"
            ? /*lang=json,strict*/ """{"model":"resolved","answers":{"bug":{"type":"noul","noul":0.9}}}"""
            : /*lang=json,strict*/ """{"model":"resolved","answers":[{"name":"bug","type":"predicate","probability":0.9}]}""";
        using HttpClient http = new(new Handler(async (message, token) =>
        {
            captured = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            return Response(response);
        }))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        DecisionClient client = new(http, Options(kind), NullLogger<DecisionClient>.Instance);

        _ = await client.EvaluateAsync(request);

        JsonElement bug = kind == "OpenRouter"
            ? captured!.RootElement.GetProperty("questions").GetProperty("bug")
            : captured!.RootElement.GetProperty("questions")[0];
        Assert.Equal("Is it broken?", bug.GetProperty("instructions").GetString());
        _ = Assert.Throws<KeyNotFoundException>(() => bug.GetProperty("criteria"));
    }

    /// <summary>
    /// The documented noul answer is a number: a literal string or boolean answer — undocumented response
    /// shapes — fail closed as InvalidResponse.
    /// </summary>
    /// <param name="answerValue">Raw JSON fragment the model emitted for the noul answer.</param>
    [Theory]
    [InlineData("\"yes\"")]
    [InlineData("true")]
    public async Task BooleanAnswer_NonNumberFailsClosed(string answerValue)
    {
        DecisionRequest request = new(/*lang=json,strict*/ "{\"ticket\":\"broken checkout\"}", [new BooleanQuestion("bug", "Is it broken?")]);
        string body = "{\"model\":\"resolved\",\"answers\":{\"bug\":{\"type\":\"noul\",\"noul\":"
            + answerValue + "}}}";
        using HttpClient http = new(new Handler((_, _) => Task.FromResult(Response(body))))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        DecisionClient client = new(http, Options(), NullLogger<DecisionClient>.Instance);

        DecisionException failure = await Assert.ThrowsAsync<DecisionException>(() => client.EvaluateAsync(request));
        Assert.Equal(DecisionFailure.InvalidResponse, failure.Failure);
    }

    /// <summary>A lone boolean pole is a composition bug and fails fast as a typed input failure.</summary>
    [Fact]
    public async Task BooleanCriteria_RequireBothPoles()
    {
        DecisionRequest request = new(/*lang=json,strict*/ "{\"ticket\":\"broken checkout\"}",
        [
            new BooleanQuestion("bug", "Is it broken?", CriteriaTrue: "Only one pole supplied."),
        ]);
        using HttpClient http = new(new Handler((_, _) => Task.FromResult(Response(/*lang=json,strict*/ """{"model":"m"}"""))))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        DecisionClient client = new(http, Options(), NullLogger<DecisionClient>.Instance);

        DecisionException failure = await Assert.ThrowsAsync<DecisionException>(() => client.EvaluateAsync(request));
        Assert.Equal(DecisionFailure.Input, failure.Failure);
    }

    /// <summary>Host prefix and optional trailing slash are preserved with exactly one operation segment.</summary>
    [Theory]
    [InlineData("OpenRouter", "https://example.invalid/api/alpha", "/api/alpha/decisions")]
    [InlineData("OpenRouter", "https://example.invalid/api/alpha/", "/api/alpha/decisions")]
    [InlineData("OpenAI", "https://example.invalid/v1", "/v1/decisions")]
    [InlineData("OpenAI", "https://example.invalid/v1/", "/v1/decisions")]
    [InlineData("OpenAI", "https://example.invalid/proxy/v1/", "/proxy/v1/decisions")]
    [InlineData("SystemOne", "http://localhost:8081/v1", "/v1/systemone")]
    [InlineData("SystemOne", "http://localhost:8081/v1/", "/v1/systemone")]
    [InlineData("SystemOne", "http://localhost:8081/proxy/v1", "/proxy/v1/systemone")]
    public async Task ComposesBasePaths(string kind, string host, string expectedPath)
    {
        using HttpClient http = new(new Handler((message, _) =>
        {
            Assert.Equal(expectedPath, message.RequestUri!.AbsolutePath);
            return Task.FromResult(Response(kind == "OpenAI" ? OpenAIResponse : RouterResponse));
        }))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        DecisionOptions options = new()
        {
            APIKind = kind,
            Host = host,
            Model = kind == "SystemOne" ? null : "configured",
            ApiKey = kind == "SystemOne" ? null : "fixture-key",
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
