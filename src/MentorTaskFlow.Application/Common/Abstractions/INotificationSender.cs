using System.Text.Json;
using MentorTaskFlow.Domain.Notifications;

namespace MentorTaskFlow.Application.Common.Abstractions;

/// <summary>Why a delivery attempt failed (TZ 18.4, table of <c>NTF-013</c>).</summary>
public enum DeliveryFailure
{
    /// <summary>An SMTP timeout, a 5xx, a Telegram 429, a network error — worth retrying.</summary>
    Temporary = 0,

    /// <summary>
    /// An invalid address, a blocked bot, an unknown chat — not worth retrying.
    /// </summary>
    /// <remarks>
    /// Retrying a permanent failure is not merely useless: five deliveries to a mailbox that does not
    /// exist are five more chances to be classified as a spam source.
    /// </remarks>
    Permanent = 1,
}

/// <summary>The outcome of one attempt.</summary>
public sealed record DeliveryResult
{
    public required bool Succeeded { get; init; }

    public string? ProviderMessageId { get; init; }

    public DeliveryFailure? Failure { get; init; }

    public string? Error { get; init; }

    public static DeliveryResult Success(string? providerMessageId = null) =>
        new() { Succeeded = true, ProviderMessageId = providerMessageId };

    public static DeliveryResult Retryable(string error) =>
        new() { Succeeded = false, Failure = DeliveryFailure.Temporary, Error = error };

    public static DeliveryResult Fatal(string error) =>
        new() { Succeeded = false, Failure = DeliveryFailure.Permanent, Error = error };
}

/// <summary>Everything a channel needs to deliver one notification.</summary>
/// <remarks>
/// The recipient's address is resolved by the worker rather than stored in the row: an address that
/// changed between enqueue and delivery should be honoured, and a payload holding personal data of the
/// recipient would violate <c>NTF-017</c> the moment the row outlived them.
/// </remarks>
public sealed record NotificationMessage(
    string EventType,
    string RecipientFullName,
    string? RecipientEmail,
    string? RecipientTelegramChatId,
    JsonDocument Payload);

/// <summary>One delivery channel (TZ 18.1).</summary>
public interface INotificationSender
{
    NotificationChannel Channel { get; }

    Task<DeliveryResult> SendAsync(NotificationMessage message, CancellationToken cancellationToken);
}
