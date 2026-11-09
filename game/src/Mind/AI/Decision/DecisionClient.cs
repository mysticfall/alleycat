using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Mind.AI.Decision;

/// <summary>Stateless client. The caller owns and reuses the supplied HttpClient (with infinite timeout).</summary>
public sealed class DecisionClient : IDecisionClient
{
    /// <summary>Maximum success response size: one MiB. Error bodies are never buffered.</summary>
    public const int MaximumResponseBytes = 1024 * 1024;
    private readonly HttpClient _http;
    private readonly DecisionSettings _settings;
    private readonly ILogger<DecisionClient> _logger;

    /// <summary>Validates settings locally; does not send requests or own the supplied transport.</summary>
    public DecisionClient(HttpClient httpClient, DecisionOptions options, ILogger<DecisionClient> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _settings = options.Validate();
        if (httpClient.Timeout != Timeout.InfiniteTimeSpan)
        {
            throw new DecisionException(DecisionFailure.Configuration, "Decision transport requires infinite HttpClient.Timeout; Decision:Timeout owns the deadline.");
        }

        _http = httpClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<DecisionResult> EvaluateAsync(DecisionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DecisionRequest snapshot = DecisionWire.Snapshot(request);
        byte[] body = DecisionWire.Write(snapshot, _settings.Kind, _settings.Model);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_settings.Timeout);
        try
        {
            using HttpRequestMessage message = new(HttpMethod.Post, _settings.Endpoint);
            if (_settings.ApiKey is not null)
            {
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);
            }
            message.Content = new ByteArrayContent(body);
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            _logger.LogDebug("Dispatching decision request using {APIKind} with {QuestionCount} questions.", _settings.Kind, snapshot.Questions.Count);
            using HttpResponseMessage response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            // Error bodies are deliberately not read or logged. Disposing closes them without unbounded buffering.
            if (!response.IsSuccessStatusCode)
            {
                throw new DecisionException(DecisionFailure.HTTP, "Decision HTTP request failed.", response.StatusCode);
            }

            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            {
                throw new DecisionException(DecisionFailure.InvalidResponse, "Decision response exceeds the size limit.");
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using MemoryStream buffer = new();
            byte[] chunk = new byte[8192];
            while (true)
            {
                int count = await stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, MaximumResponseBytes + 1 - (int)buffer.Length)), deadline.Token).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                buffer.Write(chunk, 0, count);
                if (buffer.Length > MaximumResponseBytes)
                {
                    throw new DecisionException(DecisionFailure.InvalidResponse, "Decision response exceeds the size limit.");
                }
            }
            deadline.Token.ThrowIfCancellationRequested();
            DecisionResult result = DecisionWire.Parse(buffer.ToArray(), snapshot, _settings.Kind);
            deadline.Token.ThrowIfCancellationRequested();
            _logger.LogDebug("Validated decision response containing {AnswerCount} answers.", result.Answers.Count);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Decision request cancelled.", cancellationToken);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new DecisionException(DecisionFailure.Timeout, "Decision request timed out.");
        }
        catch (OperationCanceledException)
        {
            throw new DecisionException(DecisionFailure.Transport, "Decision transport cancelled independently of the caller or deadline.");
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            throw new DecisionException(DecisionFailure.Transport, "Decision transport failed.");
        }
    }
}
