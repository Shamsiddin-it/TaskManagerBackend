using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Domain.Notifications;
using MentorTaskFlow.Infrastructure.Notifications;
using MentorTaskFlow.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MentorTaskFlow.Infrastructure.Telegram;

/// <summary>
/// Delivers a notification as a Telegram message (TZ 19.4).
/// </summary>
/// <remarks>
/// The bot API is called directly over HTTP rather than through a client library: three fields of one
/// endpoint are needed, and the error classification of 19.4 — which is the part that matters — has to
/// be written here in any case.
/// </remarks>
public sealed class TelegramNotificationSender(
    IHttpClientFactory httpClientFactory,
    IOptions<TelegramOptions> telegramOptions,
    IOptions<NotificationOptions> notificationOptions,
    ILogger<TelegramNotificationSender> logger) : INotificationSender
{
    public const string HttpClientName = "telegram";

    private readonly TelegramOptions _options = telegramOptions.Value;

    public NotificationChannel Channel => NotificationChannel.Telegram;

    public async Task<DeliveryResult> SendAsync(NotificationMessage message, CancellationToken cancellationToken)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.BotToken))
        {
            // Not a retry: the installation has no bot, and waiting will not give it one.
            return DeliveryResult.Fatal("Интеграция с Telegram отключена.");
        }

        if (string.IsNullOrWhiteSpace(message.RecipientTelegramChatId))
        {
            // NTF-001 keeps these rows from being created, so reaching here means the recipient
            // unbound between enqueue and delivery. Permanent: the row was addressed to a binding that
            // no longer exists, and the TelegramPreferred fallback covers the next event.
            return DeliveryResult.Fatal("У получателя нет привязанного Telegram.");
        }

        var rendered = NotificationTemplates.Render(
            message.EventType,
            message.Payload,
            notificationOptions.Value.AppBaseUrl);

        var client = httpClientFactory.CreateClient(HttpClientName);

        try
        {
            var response = await client.PostAsJsonAsync(
                $"/bot{_options.BotToken}/sendMessage",
                new { chat_id = message.RecipientTelegramChatId, text = rendered.PlainText },
                cancellationToken);

            return await ClassifyAsync(response, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(exception, "Telegram delivery failed at the transport level.");

            return DeliveryResult.Retryable(exception.Message);
        }
    }

    /// <summary>
    /// The table of 19.4.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A blocked bot and an unknown chat are permanent: neither becomes deliverable by waiting, and
    /// repeated sends to a user who blocked the bot are what gets a bot restricted.
    /// </para>
    /// <para>
    /// <c>TelegramChatId</c> is deliberately <b>not</b> cleared on a block. The person may unblock the
    /// bot, and clearing it would silently move them to email for good without them ever asking.
    /// </para>
    /// </remarks>
    private async Task<DeliveryResult> ClassifyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return DeliveryResult.Success();
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var description = TryReadDescription(body) ?? response.ReasonPhrase ?? "Telegram error";

        if (response.StatusCode is HttpStatusCode.TooManyRequests)
        {
            // 429 is temporary. The provider's own retry_after is respected by the backoff only when it
            // is longer, which the dispatcher handles through NextAttemptAt; here it is enough to
            // classify it as worth another try.
            return DeliveryResult.Retryable($"429: {description}");
        }

        var permanent = response.StatusCode is HttpStatusCode.Forbidden
                        || (response.StatusCode is HttpStatusCode.BadRequest
                            && description.Contains("chat not found", StringComparison.OrdinalIgnoreCase));

        if (permanent)
        {
            logger.LogWarning("Telegram refused a message permanently: {Description}.", description);

            return DeliveryResult.Fatal(description);
        }

        return DeliveryResult.Retryable(description);
    }

    private static string? TryReadDescription(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);

            return document.RootElement.TryGetProperty("description", out var description)
                ? description.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
