using System.Globalization;
using System.Net;
using AlleyCat.Mind.AI.Decision;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlleyCat.Tests.Mind.AI.Decision;

public partial class DecisionClientTests
{
    /// <summary>Validates the whole batch against caller identity, kind, rubric and numeric bounds.</summary>
    [Theory]
    [InlineData("OpenRouter", "\"noul\":0.96", "\"noul\":1.1")]
    [InlineData("OpenRouter", "\"noul\":0.96", "\"noul\":1e999")]
    [InlineData("OpenRouter", "\"noul\":0.96", "\"noul\":0.96,\"noul\":0.5")]
    [InlineData("OpenRouter", "\"bug\":{", "\"bug\":{\"type\":\"noul\",\"noul\":0.5},\"bug\":{")]
    [InlineData("OpenRouter", "\"bug\":", "\"unknown\":")]
    [InlineData("OpenRouter", "\"type\":\"noul\"", "\"type\":\"choice\"")]
    [InlineData("OpenRouter", "\"choice\":\"payments\"", "\"choice\":\"unknown\"")]
    [InlineData("OpenRouter", "\"confidence\":0.75", "\"confidence\":-0.1")]
    [InlineData("OpenRouter", "\"payments\":0.84", "\"payments\":0.5")]
    [InlineData("OpenRouter", "\"frontend\":0.16", "\"extra\":0.16")]
    [InlineData("OpenRouter", "\"0\":\"Next release\"", "\"0\":\"Incorrect rubric\"")]
    [InlineData("OpenRouter", "\"score\":1.99", "\"score\":2.01")]
    [InlineData("OpenRouter", "\"score\":1.99", "\"score\":1.5")]
    [InlineData("OpenRouter", "\"model\":\"resolved\",", "")]
    [InlineData("OpenRouter", "\"input_tokens\":476", "\"input_tokens\":-1")]
    [InlineData("OpenAI", "\"probability\":0.96", "\"probability\":-0.01")]
    [InlineData("OpenAI", "\"name\":\"bug\"", "\"name\":\"team\"")]
    [InlineData("OpenAI", "\"name\":\"bug\"", "\"name\":\"unknown\"")]
    [InlineData("OpenAI", "\"value\":2", "\"value\":3")]
    [InlineData("OpenAI", "\"label\":\"Now\"", "\"label\":\"Wrong\"")]
    [InlineData("OpenAI", "\"value\":\"frontend\"", "\"value\":\"payments\"")]
    public async Task RejectsInvalidCompleteBatch(string kind, string before, string after)
    {
        string response = (kind == "OpenRouter" ? RouterResponse : OpenAIResponse).Replace(before, after, StringComparison.Ordinal);
        using HttpClient http = new(new Handler((_, _) => Task.FromResult(Response(response))))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        DecisionException error = await Assert.ThrowsAsync<DecisionException>(() =>
            new DecisionClient(http, Options(kind), NullLogger<DecisionClient>.Instance).EvaluateAsync(Request("evidence")));
        Assert.Equal(DecisionFailure.InvalidResponse, error.Failure);
        Assert.Equal("Invalid decision response.", error.Message);
        Assert.Null(error.InnerException);
    }

    private static readonly DecisionOption[] _pairOptions = [new("frontend", "Rendering"), new("payments", "Billing")];
    private static readonly DecisionOption[] _trioOptions =
    [
        new("yellow", "A yellow hat"),
        new("red", "A red hat"),
        new("blue", "A blue hat")
    ];
    private static readonly DecisionOption[] _hostedOptions =
    [
        new("engaged", "Direct engagement"),
        new("movement", "Moving away"),
        new("looking-away", "Looking elsewhere"),
        new("too-far", "Too far away"),
        new("insufficient-evidence", "Insufficient evidence")
    ];

    /// <summary>
    /// Distribution sums may miss one symmetrically by 0.005 per supplied option plus 1e-9, admitting quantised and
    /// mixed-precision provider rounding (the kev/drex trio rows quote observed hosted output) in both directions
    /// while rejecting gross malformation.
    /// </summary>
    [Theory]
    [InlineData("hosted", "\"engaged\":0.67,\"movement\":0,\"looking-away\":0.28,\"too-far\":0.01,\"insufficient-evidence\":0.03", true)]
    [InlineData("hosted", "\"engaged\":0.65,\"movement\":0,\"looking-away\":0.28,\"too-far\":0.01,\"insufficient-evidence\":0.03", false)]
    [InlineData("hosted", "\"engaged\":0.58,\"movement\":0,\"looking-away\":0.28,\"too-far\":0.01,\"insufficient-evidence\":0.03", false)]
    [InlineData("trio", "\"yellow\":0.9963,\"red\":0.0026,\"blue\":0.0012", true)]
    [InlineData("trio", "\"yellow\":0.9983,\"red\":0.0011,\"blue\":0.0005", true)]
    [InlineData("pair", "\"payments\":0.85,\"frontend\":0.16", true)]
    [InlineData("pair", "\"payments\":0.83,\"frontend\":0.16", true)]
    [InlineData("pair", "\"payments\":0.86,\"frontend\":0.16", false)]
    [InlineData("pair", "\"payments\":0.675,\"frontend\":0.315", true)]
    public async Task DistributionSumsAllowHalfPercentPerOption(string scenario, string probabilities, bool accepted)
    {
        (DecisionOption[] options, string selected) = scenario switch
        {
            "hosted" => (_hostedOptions, "engaged"),
            "trio" => (_trioOptions, "yellow"),
            _ => (_pairOptions, "payments")
        };
        await EvaluateChoiceDistribution(probabilities, selected, accepted, options);
    }

    private const string ChoiceDistributionBody = """
        {"model":"resolved","answers":{"attention":{"type":"choice","choice":"@selected","probabilities":{@probabilities}}}}
        """;

    /// <summary>Evaluates one choice question carrying the supplied probability map and asserts the outcome.</summary>
    private static async Task EvaluateChoiceDistribution(string probabilities, string selected, bool accepted, DecisionOption[] options)
    {
        string response = ChoiceDistributionBody
            .Replace("@selected", selected, StringComparison.Ordinal)
            .Replace("@probabilities", probabilities, StringComparison.Ordinal);
        using HttpClient http = new(new Handler((_, _) => Task.FromResult(Response(response))))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        DecisionClient client = new(http, Options(), NullLogger<DecisionClient>.Instance);
        DecisionRequest request = new("evidence", [new ChoiceQuestion("attention", "Where is the attention?", options)]);
        if (accepted)
        {
            ChoiceAnswer choice = Assert.IsType<ChoiceAnswer>((await client.EvaluateAsync(request)).Answers[0]);
            Assert.Equal(options.Length, choice.Probabilities!.Count);
        }
        else
        {
            DecisionException error = await Assert.ThrowsAsync<DecisionException>(() => client.EvaluateAsync(request));
            Assert.Equal(DecisionFailure.InvalidResponse, error.Failure);
        }
    }

    private const string RouterOffSumBatch = /*lang=json,strict*/ """
        {"model":"resolved","answers":{"bug":{"type":"noul","noul":0.96},
         "team":{"type":"choice","choice":"payments","probabilities":{"payments":0.51,"frontend":0.5}},
         "urgency":{"type":"score","score":1.01,"probabilities":{"0":0.34,"1":0.33,"2":0.34}}}}
        """;
    private const string OpenAIOffSumBatch = /*lang=json,strict*/ """
        {"model":"resolved","answers":[{"name":"bug","type":"predicate","probability":0.96},
         {"name":"team","type":"choice","choice":"payments","probabilities":[{"value":"payments","probability":0.51},{"value":"frontend","probability":0.5}]},
         {"name":"urgency","type":"score","score":1.01,"probabilities":[{"value":0,"probability":0.34},{"value":1,"probability":0.33},{"value":2,"probability":0.34}]}]}
        """;

    /// <summary>Both dialects accept off-sum choice and score distributions inside one multi-question batch.</summary>
    [Theory]
    [InlineData("OpenRouter")]
    [InlineData("OpenAI")]
    public async Task MixedBatchesAcceptOffSumDistributions(string kind)
    {
        using HttpClient http = new(new Handler((_, _) => Task.FromResult(Response(kind == "OpenRouter" ? RouterOffSumBatch : OpenAIOffSumBatch))))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        DecisionResult result = await new DecisionClient(http, Options(kind), NullLogger<DecisionClient>.Instance).EvaluateAsync(Request("evidence"));
        ChoiceAnswer choice = Assert.IsType<ChoiceAnswer>(result.ByName["team"]);
        Assert.Equal(0.5, choice.Probabilities!["frontend"]);
        Assert.Equal(0.51, choice.Probabilities!["payments"]);
        ScoreAnswer score = Assert.IsType<ScoreAnswer>(result.ByName["urgency"]);
        Assert.Equal(1.01, score.Score);
        Assert.Equal([0.34, 0.33, 0.34], score.Probabilities!);
    }

    /// <summary>A score distribution beyond the per-level sum allowance fails on the sum alone.</summary>
    [Fact]
    public async Task ScoreDistributionBeyondSumAllowanceIsRejected()
    {
        const string response = /*lang=json,strict*/ """
            {"model":"resolved","answers":{"urgency":{"type":"score","score":0.45,"probabilities":{"0":0.6,"1":0.45}}}}
            """;
        using HttpClient http = new(new Handler((_, _) => Task.FromResult(Response(response))))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        DecisionRequest request = new("evidence", [new ScoreQuestion("urgency", "How urgent?", ["Low", "High"])]);
        DecisionException error = await Assert.ThrowsAsync<DecisionException>(() =>
            new DecisionClient(http, Options(), NullLogger<DecisionClient>.Instance).EvaluateAsync(request));
        Assert.Equal(DecisionFailure.InvalidResponse, error.Failure);
    }

    private const string ScoreConsistencyBody = """
        {"model":"resolved","answers":{"urgency":{"type":"score","score":@score,"probabilities":{"0":0,"1":0.01,"2":0.99}}}}
        """;

    /// <summary>
    /// A score distribution must reproduce the reported score within 0.005 per supplied level plus 1e-9; drift
    /// beyond the allowance (including just-past-boundary 1.974) is rejected while in-range drift up to the exact
    /// boundary 1.975 is admitted.
    /// </summary>
    [Theory]
    [InlineData("1.5", false)]
    [InlineData("1.974", false)]
    [InlineData("1.975", true)]
    [InlineData("2", true)]
    public async Task ScoreDistributionConsistencyAllowance(string reported, bool accepted)
    {
        string response = ScoreConsistencyBody.Replace("@score", reported, StringComparison.Ordinal);
        using HttpClient http = new(new Handler((_, _) => Task.FromResult(Response(response))))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        DecisionRequest request = new("evidence", [new ScoreQuestion("urgency", "How urgent?", ["Next release", "This week", "Now"])]);
        DecisionClient client = new(http, Options(), NullLogger<DecisionClient>.Instance);
        if (accepted)
        {
            ScoreAnswer score = Assert.IsType<ScoreAnswer>((await client.EvaluateAsync(request)).Answers[0]);
            Assert.Equal(double.Parse(reported, CultureInfo.InvariantCulture), score.Score);
        }
        else
        {
            DecisionException error = await Assert.ThrowsAsync<DecisionException>(() => client.EvaluateAsync(request));
            Assert.Equal(DecisionFailure.InvalidResponse, error.Failure);
        }
    }

    /// <summary>Refusals retain data but are never converted into a decision or implicit diagnostic text.</summary>
    [Fact]
    public async Task RefusalAndAbsentOptionalFieldsRemainExplicit()
    {
        const string response = /*lang=json,strict*/ """
            {"model":"resolved","answers":[{"name":"bug","type":"refusal","refusal":"sensitive provider detail"},
            {"name":"team","type":"choice","choice":"payments"},{"name":"urgency","type":"score","score":1.25}]}
            """;
        using HttpClient http = new(new Handler((_, _) => Task.FromResult(Response(response))))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        DecisionResult result = await new DecisionClient(http, Options("OpenAI"), NullLogger<DecisionClient>.Instance).EvaluateAsync(Request("evidence"));
        DecisionRefusal refusal = Assert.IsType<DecisionRefusal>(result.Answers[0]);
        Assert.Equal("sensitive provider detail", refusal.Detail.GetProperty("refusal").GetString());
        Assert.DoesNotContain("sensitive", refusal.ToString());
        Assert.Null(Assert.IsType<ChoiceAnswer>(result.Answers[1]).Probabilities);
        Assert.Null(Assert.IsType<ScoreAnswer>(result.Answers[2]).Probabilities);
        Assert.Null(result.Cost);
        Assert.Null(result.Provider);
    }

    /// <summary>Malformed JSON, missing answers and oversized success bodies fail without body diagnostics.</summary>
    [Theory]
    [InlineData("malformed")]
    [InlineData("missing")]
    [InlineData("oversized")]
    [InlineData("streamed-oversized")]
    public async Task RejectsMalformedMissingAndOversizedBodies(string scenario)
    {
        string body = scenario switch
        {
            "malformed" => "private raw provider body",
            "missing" => /*lang=json,strict*/ "{\"model\":\"resolved\",\"answers\":{}}",
            _ => new string('x', DecisionClient.MaximumResponseBytes + 1)
        };
        using HttpClient http = new(new Handler((_, _) =>
        {
            HttpResponseMessage response = Response(body);
            if (scenario == "streamed-oversized")
            {
                response.Content = new StreamContent(new NonSeekableStream(System.Text.Encoding.UTF8.GetBytes(body)));
            }

            return Task.FromResult(response);
        }))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        DecisionException error = await Assert.ThrowsAsync<DecisionException>(() =>
            new DecisionClient(http, Options(), NullLogger<DecisionClient>.Instance).EvaluateAsync(Request("evidence")));
        Assert.Equal(DecisionFailure.InvalidResponse, error.Failure);
        Assert.DoesNotContain("private", error.ToString());
    }

    /// <summary>HTTP status survives even huge error bodies; no body is read and no automatic retry occurs.</summary>
    [Fact]
    public async Task HTTPFailureDoesNotReadOrLeakBody()
    {
        int calls = 0;
        using HttpClient http = new(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StreamContent(new NeverReadStream()) });
        }))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        DecisionException error = await Assert.ThrowsAsync<DecisionException>(() =>
            new DecisionClient(http, Options(), NullLogger<DecisionClient>.Instance).EvaluateAsync(Request("private evidence")));
        Assert.Equal(DecisionFailure.HTTP, error.Failure);
        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Equal(1, calls);
        Assert.Null(error.InnerException);
    }

    /// <summary>Cancellation and the deadline cover both handler dispatch and stream reading.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CancellationAndTimeoutCoverDispatchAndRead(bool duringRead, bool timeout)
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource caller = new();
        using HttpClient http = new(new Handler(async (_, token) =>
        {
            if (duringRead)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BlockingStream(entered)) };
            }

            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Unreachable");
        }))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        Task<DecisionResult> pending = new DecisionClient(http, Options(timeout: timeout ? 0.05 : 5), NullLogger<DecisionClient>.Instance)
            .EvaluateAsync(Request("evidence"), caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (timeout)
        {
            DecisionException error = await Assert.ThrowsAsync<DecisionException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(DecisionFailure.Timeout, error.Failure);
        }
        else
        {
            await caller.CancelAsync();
            OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(caller.Token, error.CancellationToken);
        }
    }

    /// <summary>Transport errors never include underlying provider/credential details.</summary>
    [Fact]
    public async Task TransportFailureHasSafeTypedDiagnostic()
    {
        using HttpClient http = new(new Handler((_, _) => throw new HttpRequestException("fixture-key private body")))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        DecisionException error = await Assert.ThrowsAsync<DecisionException>(() =>
            new DecisionClient(http, Options(), NullLogger<DecisionClient>.Instance).EvaluateAsync(Request("evidence")));
        Assert.Equal(DecisionFailure.Transport, error.Failure);
        Assert.DoesNotContain("fixture-key", error.ToString());
        Assert.Null(error.InnerException);
    }

    /// <summary>Invalid or unsupported input is rejected before dispatch.</summary>
    [Theory]
    [InlineData("primitive")]
    [InlineData("duplicate")]
    [InlineData("empty")]
    [InlineData("score")]
    [InlineData("choice")]
    public async Task InputValidationPrecedesNetwork(string scenario)
    {
        int calls = 0;
        using HttpClient http = new(new Handler((_, _) => { calls++; throw new InvalidOperationException("No call expected"); }))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        DecisionRequest request = scenario switch
        {
            "primitive" => Request(42),
            "duplicate" => new("evidence", [new BooleanQuestion("same", "Condition"), new BooleanQuestion("same", "Condition")]),
            "empty" => new("evidence", []),
            "score" => new("evidence", [new ScoreQuestion("score", "Rubric", ["Only one"])]),
            _ => new("evidence", [new ChoiceQuestion("choice", "Options", [new("same", "One"), new("same", "Two")])])
        };
        DecisionException error = await Assert.ThrowsAsync<DecisionException>(() => new DecisionClient(http, Options(), NullLogger<DecisionClient>.Instance).EvaluateAsync(request));
        Assert.Equal(scenario == "primitive" ? DecisionFailure.Unsupported : DecisionFailure.Input, error.Failure);
        Assert.Equal(0, calls);
    }

    private class NeverReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException(); set => throw new NotSupportedException();
        }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Error body must not be read.");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BlockingStream(TaskCompletionSource entered) : NeverReadStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _ = entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    private sealed class NonSeekableStream(byte[] body) : MemoryStream(body)
    {
        public override bool CanSeek => false;
    }
}
