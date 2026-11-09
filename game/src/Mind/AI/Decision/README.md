# Decision Client

AI-014 owns the [contract](../../../../../specs/ai/014-decision-model-api/index.md). `Game` registers the provider;
standalone consumers can call `services.AddDecisionClient()` after registering merged `IConfiguration` and logging.
Registration/provider construction is inert; explicit `GetClient()` validates the separate `Decision` settings.
The provider owns its reusable HTTP transport until DI disposal. Direct `DecisionClient` consumers own their supplied
`HttpClient`, which must use infinite timeout and should disable redirects and automatic retries.
`Decision:Timeout` is positive finite seconds, up to `4294967` (the managed cancellation timer's supported range).

```csharp
using AlleyCat.Mind.AI.Decision;
using Microsoft.Extensions.DependencyInjection;

// Explicit opt-in: this sends a request only when invoked by the caller.
IDecisionClient client = services.GetRequiredService<DecisionClientProvider>().GetClient();
DecisionResult result = await client.EvaluateAsync(new DecisionRequest(
    "The player has moved away while the NPC was talking.",
    [new BooleanQuestion("engaged", "Is the player still engaged in this conversation?")]), cancellationToken);
if (result.ByName["engaged"] is BooleanAnswer answer)
{
    double probability = answer.Probability; // Threshold/action policy belongs to the caller.
}
else if (result.ByName["engaged"] is DecisionRefusal refusal)
{
    // Explicit refusal; Detail is sensitive provider data, not something to log implicitly.
}
```

`ChoiceQuestion` takes ordered `DecisionOption(value, criteria)` entries; `ScoreQuestion` takes 2–10 ordered criteria.
For structured evidence pass a `JsonElement` object/array, not serialised text. Inputs are snapshotted before dispatch.
Answers retain caller question/option order; score probabilities use zero-based level order. A score is a fractional
weighted index, not a selected category or a normalised rating. Missing optional metadata remains null.

`OperationCanceledException` means caller cancellation. `DecisionException.Failure` distinguishes configuration, input,
unsupported features, timeout, transport, HTTP and invalid-response failures; `StatusCode` is present for HTTP failures.
Refusals remain typed per question. No partial result is returned when any answer is invalid. Diagnostics omit bodies,
credentials and provider refusal/error text. Success responses are limited to 1 MiB; error bodies are not read/buffered.
Distributions must be complete and sum to one within a single symmetric allowance: the sum may deviate from one in
either direction by at most `0.005` per supplied option/level plus `1e-9`. Supplied score distributions must reproduce
the score within the same allowance, `0.005` per supplied level plus `1e-9`.
No clamping, retries or startup requests are performed.

Fixture verification establishes dialect compatibility only, not hosted model quality, latency or gameplay outcomes.
