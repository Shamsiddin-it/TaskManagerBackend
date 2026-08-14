using System.Net.Sockets;
using MailKit.Net.Smtp;
using MailKit.Security;
using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Domain.Notifications;
using MentorTaskFlow.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace MentorTaskFlow.Infrastructure.Notifications;

/// <inheritdoc />
public sealed class SmtpNotificationSender(
    IOptions<NotificationOptions> options,
    ILogger<SmtpNotificationSender> logger) : INotificationSender
{
    private readonly NotificationOptions _options = options.Value;

    public NotificationChannel Channel => NotificationChannel.Email;

    public async Task<DeliveryResult> SendAsync(NotificationMessage message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message.RecipientEmail))
        {
            // Permanent by nature: an account without an address will not acquire one by waiting, and
            // the row would otherwise consume all five attempts to prove it.
            return DeliveryResult.Fatal("У получателя нет адреса электронной почты.");
        }

        var rendered = NotificationTemplates.Render(message.EventType, message.Payload, _options.AppBaseUrl);

        var mail = new MimeMessage
        {
            Subject = rendered.Subject,
            Body = new BodyBuilder { TextBody = rendered.PlainText, HtmlBody = rendered.Html }.ToMessageBody(),
        };

        mail.From.Add(new MailboxAddress(_options.FromName, _options.FromAddress));
        mail.To.Add(new MailboxAddress(message.RecipientFullName, message.RecipientEmail));

        using var client = new SmtpClient();

        try
        {
            await client.ConnectAsync(
                _options.SmtpHost,
                _options.SmtpPort,
                _options.SmtpUseSsl ? SecureSocketOptions.StartTls : SecureSocketOptions.None,
                cancellationToken);

            if (!string.IsNullOrEmpty(_options.SmtpUser))
            {
                await client.AuthenticateAsync(_options.SmtpUser, _options.SmtpPassword, cancellationToken);
            }

            var response = await client.SendAsync(mail, cancellationToken);
            await client.DisconnectAsync(quit: true, cancellationToken);

            return DeliveryResult.Success(response);
        }
        catch (SmtpCommandException exception) when (IsPermanent(exception))
        {
            // 5.1.x is the mailbox saying it does not exist. NTF-013 sends this straight to the dead
            // letter: retrying is useless and repeated delivery to unknown addresses is how a sender
            // acquires a reputation as a spam source.
            logger.LogWarning(exception, "SMTP rejected the recipient permanently.");

            return DeliveryResult.Fatal($"SMTP {exception.StatusCode}: {exception.Message}");
        }
        catch (Exception exception) when (exception is SmtpCommandException
                                              or SmtpProtocolException
                                              or IOException
                                              or SocketException
                                              or OperationCanceledException)
        {
            logger.LogWarning(exception, "SMTP delivery failed temporarily.");

            return DeliveryResult.Retryable(exception.Message);
        }
    }

    /// <summary>
    /// A 5.1.x enhanced status code, or a 5xx reply about the mailbox itself (<c>NTF-013</c>).
    /// </summary>
    /// <remarks>
    /// Deliberately narrow. A 5xx that is not about the recipient — a server refusing everything
    /// during maintenance — stays retryable, because treating it as permanent would silently discard
    /// every notification raised in that window.
    /// </remarks>
    private static bool IsPermanent(SmtpCommandException exception) =>
        exception.ErrorCode is SmtpErrorCode.RecipientNotAccepted or SmtpErrorCode.SenderNotAccepted
        && (int)exception.StatusCode >= 500;
}
