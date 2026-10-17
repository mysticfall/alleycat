#pragma warning disable OPENAI001 // The OpenAI Responses APIs are experimental in the SDK.

using System.ClientModel.Primitives;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

namespace AlleyCat.Mind.AI.Provider;

/// <summary>
/// AI-owned response normaliser for the OpenAI Responses path: surfaces reasoning text the provider
/// returned directly in reasoning content parts of type <c>reasoning_text</c>, which the installed
/// Microsoft.Extensions.AI OpenAI adapter drops because it maps summary text only (AI-002 TR-32–TR-33).
/// </summary>
/// <remarks>
/// Normalisation runs at the provider boundary — before runner handling, AI diagnostics, or AI-011
/// recording — and only fills the parsed <see cref="TextReasoningContent.Text" /> of the response the
/// client already produced. Protected data, item identity, raw representations, content order, and
/// continuation semantics are preserved in place, and the recorder keeps its parsed-only boundary.
/// Already-surfaced summary text keeps its place first and recognised content parts follow in their
/// original part order, with a newline separating consecutive parts; when the complete recognised
/// content text is exactly identical to the summary text, no second copy is added. Streaming responses
/// are passed through unchanged: the Mind runtime consumes non-streaming responses only.
/// </remarks>
internal sealed class ResponsesReasoningTextNormalisingChatClient(IChatClient inner) : IChatClient
{
    private const string PartSeparator = "\n";

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ChatResponse response = await inner.GetResponseAsync(messages, options, cancellationToken);
        Normalise(response);
        return response;
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType == typeof(ResponsesReasoningTextNormalisingChatClient) ? this
            : inner.GetService(serviceType, serviceKey);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => inner.GetStreamingResponseAsync(messages, options, cancellationToken);

    void IDisposable.Dispose()
    {
        inner.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void Normalise(ChatResponse response)
    {
        foreach (ChatMessage message in response.Messages)
        {
            foreach (AIContent content in message.Contents)
            {
                if (content is TextReasoningContent reasoning
                    && reasoning.RawRepresentation is ReasoningResponseItem item)
                {
                    AppendRecognisedContentText(reasoning, item);
                }
            }
        }
    }

    /// <summary>
    /// Appends the item's recognised content text after any summary text the adapter already surfaced,
    /// in place, without touching protected data, item identity, or content order.
    /// </summary>
    private static void AppendRecognisedContentText(TextReasoningContent reasoning, ReasoningResponseItem item)
    {
        string contentText = ReadRecognisedContentText(item);
        if (contentText.Length == 0)
        {
            // No recognised content text: the parsed response stays exactly as the adapter produced it,
            // and reasoning text is never fabricated from token counts or metadata (AI-002 TR-31).
            return;
        }

        string summaryText = reasoning.Text ?? string.Empty;
        if (string.Equals(summaryText, contentText, StringComparison.Ordinal))
        {
            // Whole-text identity: the surfaced summary already carries exactly this content text, so no
            // second copy is added (AI-002 TR-33).
            return;
        }

        reasoning.Text = summaryText.Length == 0 ? contentText : summaryText + PartSeparator + contentText;
    }

    /// <summary>
    /// Reads the reasoning item's recognised content parts. The typed item surface exposes no content
    /// property (OpenAI 2.13.0), but <see cref="ModelReaderWriter" /> round-trips the JSON the SDK
    /// retained for them, so only parts of type <c>reasoning_text</c> carrying a string text are
    /// surfaced, joined in their original part order; unknown or malformed parts are skipped without
    /// disturbing the summary and function-call results already parsed from the same response.
    /// </summary>
    private static string ReadRecognisedContentText(ReasoningResponseItem item)
    {
        try
        {
            using var document = JsonDocument.Parse(ModelReaderWriter.Write(item).ToMemory());
            if (!document.RootElement.TryGetProperty("content", out JsonElement content)
                || content.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            List<string>? parts = null;
            foreach (JsonElement part in content.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object
                    || !part.TryGetProperty("type", out JsonElement type)
                    || type.GetString() != "reasoning_text"
                    || !part.TryGetProperty("text", out JsonElement text)
                    || text.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string value = text.GetString() ?? string.Empty;
                if (value.Length > 0)
                {
                    (parts ??= []).Add(value);
                }
            }

            return parts is null ? string.Empty : string.Join(PartSeparator, parts);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or ArgumentException)
        {
            // Contained: when the retained representation cannot be read, the parsed response keeps the
            // adapter's own summary and call results unchanged.
            return string.Empty;
        }
    }
}
