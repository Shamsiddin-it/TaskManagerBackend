using System.Diagnostics;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Application.Common.Exceptions;
using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Infrastructure.Observability;
using MentorTaskFlow.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MentorTaskFlow.Infrastructure.Analytics;

/// <summary>
/// The Anthropic implementation of <see cref="IAiSummaryProvider"/> (<c>AI-001</c>).
/// </summary>
/// <remarks>
/// <para>
/// Retries are ours, not the SDK's: the SDK retries twice by default with its own backoff, and left
/// on it would silently multiply the attempt count and blow through the ninety-second budget of
/// <c>AI-003</c> while appearing to obey it. The client is constructed with retries disabled so this
/// class is the only thing counting.
/// </para>
/// <para>
/// Thinking is switched off. <c>AI-002</c> caps the response at 1 500 tokens, and on a model where
/// thinking is on by default that budget is shared — a summary would be truncated mid-sentence by
/// reasoning the reader never sees. Nothing here needs multi-step reasoning: the figures arrive
/// already computed and the task is to describe them.
/// </para>
/// </remarks>
public sealed class AnthropicSummaryProvider(
    AnthropicClient client,
    IOptions<AiOptions> options,
    AiMetrics metrics,
    AiProviderStatus status,
    IClock clock,
    ILogger<AnthropicSummaryProvider> logger) : IAiSummaryProvider
{
    private readonly AiOptions _options = options.Value;

    public string ModelId => _options.ModelId;

    public string PromptVersion => _options.PromptVersion;

    public bool IsConfigured => _options.IsConfigured;

    public async Task<AiSummaryCompletion> GenerateAsync(
        AiSummaryPrompt prompt,
        CancellationToken cancellationToken)
    {
        var budget = TimeSpan.FromSeconds(_options.TotalBudgetSeconds);
        var elapsed = Stopwatch.StartNew();

        using var budgetSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budgetSource.CancelAfter(budget);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var completion = await CallAsync(prompt, budgetSource.Token);

                status.RecordSuccess();
                metrics.Tokens(_options.ModelId, completion.InputTokens, completion.OutputTokens);

                return completion;
            }
            catch (Exception exception) when (IsRetryable(exception, cancellationToken))
            {
                var isLastAttempt = attempt >= _options.MaxRetries;
                var delay = AiOptions.RetryDelays[Math.Min(attempt, AiOptions.RetryDelays.Count - 1)];

                // Not started if it cannot finish: a retry launched with four seconds of budget left
                // reaches the timeout instead of the provider, and the caller waits for nothing.
                if (isLastAttempt || elapsed.Elapsed + delay >= budget)
                {
                    throw Unavailable(exception, attempt + 1);
                }

                logger.LogWarning(
                    exception,
                    "AI provider attempt {Attempt} failed; retrying in {Delay}.",
                    attempt + 1,
                    delay);

                await Task.Delay(delay, budgetSource.Token);
            }
            catch (Exception exception) when (exception is not AppException and not OperationCanceledException)
            {
                // A permanent error — a malformed request, a rejected key. Retrying cannot change the
                // answer, and AI-003 says so explicitly.
                throw Unavailable(exception, attempt + 1);
            }
        }
    }

    private async Task<AiSummaryCompletion> CallAsync(AiSummaryPrompt prompt, CancellationToken cancellationToken)
    {
        using var attemptSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptSource.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        var response = await client.Messages.Create(
            new MessageCreateParams
            {
                Model = _options.ModelId,
                MaxTokens = _options.MaxOutputTokens,
                Thinking = new ThinkingConfigDisabled(),
                System = new List<TextBlockParam> { new() { Text = prompt.SystemInstructions } },
                Messages = [new() { Role = Role.User, Content = prompt.Data }],
            },
            attemptSource.Token);

        var content = string.Concat(
            response.Content
                .Select(block => block.Value)
                .OfType<TextBlock>()
                .Select(text => text.Text));

        if (string.IsNullOrWhiteSpace(content))
        {
            // A refusal or an empty completion is not a summary. Reported as unavailable rather than
            // stored as an empty report: an empty cached report would be served for the whole period.
            throw new InvalidOperationException($"Провайдер вернул пустой ответ (stop_reason={response.StopReason}).");
        }

        return new AiSummaryCompletion(
            content,
            (int?)response.Usage?.InputTokens,
            (int?)response.Usage?.OutputTokens,
            response.ID);
    }

    /// <summary>
    /// 429, 5xx and timeouts are retried; everything else is not (<c>AI-003</c>).
    /// </summary>
    /// <remarks>
    /// The caller's own cancellation is deliberately excluded: the client has gone away, and the
    /// retries would be spent on a response nobody is waiting for.
    /// </remarks>
    private static bool IsRetryable(Exception exception, CancellationToken callerToken) =>
        !callerToken.IsCancellationRequested
        && exception is AnthropicRateLimitException
            or Anthropic5xxException
            or AnthropicIOException
            or TaskCanceledException
            or TimeoutException;

    private ServiceUnavailableException Unavailable(Exception exception, int attempts)
    {
        status.RecordFailure(clock.UtcNow);
        metrics.Failure(exception.GetType().Name);

        logger.LogError(exception, "AI provider gave up after {Attempts} attempt(s).", attempts);

        // The provider's own message never reaches the caller: it can quote the prompt back, and the
        // prompt carries the organization's data (AUD-022, SEC-021).
        return new ServiceUnavailableException(
            ErrorCodes.AiProviderUnavailable,
            "AI-провайдер сейчас недоступен. Метрики отчёта доступны без резюме.");
    }
}

/// <summary>
/// Stands in when the feature is on but nothing is configured to call.
/// </summary>
/// <remarks>
/// Exists so composition never has a hole: the service always has a provider, and a deployment
/// without an API key answers 503 on the summary endpoint alone while every metric endpoint keeps
/// working (<c>AI-018</c>, <c>TEST-AI-002</c>). Открытый вопрос 5 — the key and the budget — is
/// answered by configuration, not by code, and this is what code does until it is.
/// </remarks>
public sealed class UnconfiguredSummaryProvider(IOptions<AiOptions> options) : IAiSummaryProvider
{
    private readonly AiOptions _options = options.Value;

    public string ModelId => _options.ModelId;

    public string PromptVersion => _options.PromptVersion;

    public bool IsConfigured => false;

    public Task<AiSummaryCompletion> GenerateAsync(AiSummaryPrompt prompt, CancellationToken cancellationToken) =>
        throw new ServiceUnavailableException(
            ErrorCodes.AiProviderUnavailable,
            "AI-провайдер не настроен в этой установке. Метрики отчёта доступны без резюме.");
}
