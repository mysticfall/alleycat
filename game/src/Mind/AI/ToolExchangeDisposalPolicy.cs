using Microsoft.Extensions.AI;

namespace AlleyCat.Mind.AI;

/// <summary>
/// Per-function exchange-disposal policy registered when a session tool is composed and consulted by the session
/// runner once a response's tool batch fully settles (AI-002 TR-17): a function opted into disposal marks its
/// complete exchange — the accepted assistant message batch, including any text or reasoning content, plus the
/// appended tool-result message (TR-19) — for removal from the replayed transcript, because the exchange's settled
/// outcome is protocol bookkeeping that later requests must never carry (TR-22). The runner understands only this
/// generic descriptor — never a concrete production tool, function name, or tool type.
/// </summary>
/// <param name="DisposesExchange">
/// Whether completed batches targeting this function are disposed: a batch is disposed only when every call in it
/// targets a disposal-opted function (TR-20), so a single non-opted call retains the whole exchange and no request
/// ever carries a dangling call or result. Session-wide call-ID validation is independent of this policy: a replayed
/// call ID from a disposed batch is still rejected as a duplicate (TR-21).
/// </param>
internal sealed record ToolExchangeDisposalPolicy(bool DisposesExchange)
{
    /// <summary>
    /// Conservative default: completed exchanges are retained — watch tools, authored extras, and unknown tools all
    /// keep their settled exchanges in the replayed transcript.
    /// </summary>
    public static ToolExchangeDisposalPolicy Retain { get; } = new(DisposesExchange: false);

    /// <summary>Policy for tools whose settled exchanges are disposed once every call in the batch settles.</summary>
    public static ToolExchangeDisposalPolicy Dispose { get; } = new(DisposesExchange: true);

    /// <summary>Binds this policy to a composed function so the session runner consults it at batch settlement.</summary>
    public AIFunction Bind(AIFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        return new ExchangeDisposalBoundFunction(function, this);
    }
}

/// <summary>
/// Composed function carrying its composition-registered exchange-disposal policy: the session runner
/// pattern-matches this carrier — and nothing about the wrapped function's identity — when a tool batch settles.
/// </summary>
internal sealed class ExchangeDisposalBoundFunction(AIFunction function, ToolExchangeDisposalPolicy policy)
    : ComposedAIFunction(function)
{
    public ToolExchangeDisposalPolicy Policy { get; } = policy;
}
