using System.ComponentModel.DataAnnotations;

namespace MentorTaskFlow.Infrastructure.Options;

/// <summary>
/// Outbox delivery and the SMTP channel (Приложение L).
/// </summary>
/// <remarks>
/// <see cref="EnableWorker"/> defaults to <see langword="false"/> so that a process which is not the
/// worker never competes for the queue. <c>DEPLOY-013</c> puts background processing in
/// <c>mtf-worker</c> alone, and scaling the API must not multiply delivery attempts.
/// </remarks>
public sealed class NotificationOptions
{
    public const string SectionName = "Notifications";

    public bool EnableWorker { get; init; }

    /// <summary>Poll interval of the delivery loop (<c>NTF-011</c>).</summary>
    [Range(1, 3600)]
    public int PollSeconds { get; init; } = 30;

    /// <summary>How often expired leases are swept back into the queue (<c>NTF-012</c>).</summary>
    [Range(1, 3600)]
    public int LeaseSweepSeconds { get; init; } = 300;

    [Range(1, 500)]
    public int BatchSize { get; init; } = 50;

    [Required]
    public string SmtpHost { get; init; } = null!;

    [Range(1, 65535)]
    public int SmtpPort { get; init; } = 25;

    public string? SmtpUser { get; init; }

    public string? SmtpPassword { get; init; }

    public bool SmtpUseSsl { get; init; }

    [Required]
    [EmailAddress]
    public string FromAddress { get; init; } = "noreply@mentortaskflow.local";

    [Required]
    public string FromName { get; init; } = "MentorTaskFlow";

    /// <summary>Where the links in messages point. Never a storage URL (<c>NTF-017</c>).</summary>
    [Required]
    public string AppBaseUrl { get; init; } = "http://localhost:5173";
}
