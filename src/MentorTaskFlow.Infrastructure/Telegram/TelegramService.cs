using System.Text.Json;
using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Application.Common.Exceptions;
using MentorTaskFlow.Application.Common.Security;
using MentorTaskFlow.Application.Common.Tenancy;
using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Contracts.Telegram;
using MentorTaskFlow.Domain.Auditing;
using MentorTaskFlow.Domain.Identity;
using MentorTaskFlow.Infrastructure.Options;
using MentorTaskFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MentorTaskFlow.Infrastructure.Telegram;

/// <inheritdoc />
public sealed class TelegramService(
    MentorTaskFlowDbContext dbContext,
    ICurrentUserAccessor currentUser,
    ISecureTokenService tokens,
    IAuditWriter auditWriter,
    IMemoryCache cache,
    IOptions<TelegramOptions> options,
    ILogger<TelegramService> logger,
    IClock clock) : ITelegramService
{
    private readonly TelegramOptions _options = options.Value;

    public async Task<TelegramBindTokenDto> IssueBindTokenAsync(CancellationToken cancellationToken)
    {
        var actor = RequireActor();
        var now = clock.UtcNow;

        await EnsureIssueRateAsync(actor.UserId, now, cancellationToken);

        // TG-006: the previous live token stops working. The partial unique index refuses a second
        // active row anyway, so this is what turns that constraint into the intended behaviour rather
        // than an error.
        var previous = await dbContext.TelegramBindTokens
            .Where(t => t.UserId == actor.UserId && t.UsedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var token in previous)
        {
            token.Invalidate(now);
        }

        var (plainToken, tokenHash) = tokens.Generate();
        var issued = TelegramBindToken.Issue(actor.UserId, tokenHash, now);

        dbContext.TelegramBindTokens.Add(issued);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
                                                  {
                                                      SqlState: PostgresErrorCodes.UniqueViolation,
                                                      ConstraintName: "ux_telegram_bind_tokens_active",
                                                  })
        {
            // Scenario 13 of Приложение K: two simultaneous issues. Exactly one token stays live, and
            // the loser is told to try again rather than leaving two working links to one account.
            throw new ConflictException(
                ErrorCodes.ResourceAlreadyExists,
                "Ссылка привязки уже выпускается. Повторите запрос.");
        }

        // The plain token is returned and immediately forgotten: it appears in no log, no audit record
        // and no metric (TG-013).
        return new TelegramBindTokenDto(
            plainToken,
            $"https://t.me/{_options.BotUsername}?start={plainToken}",
            issued.ExpiresAt);
    }

    public async Task<TelegramStatusDto> GetStatusAsync(CancellationToken cancellationToken)
    {
        var actor = RequireActor();

        var isBound = await dbContext.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(u => u.Id == actor.UserId && u.TelegramChatId != null, cancellationToken);

        if (!isBound)
        {
            return new TelegramStatusDto(false, null);
        }

        // Derived from the token that was redeemed rather than stored on the user: the moment of
        // binding is already recorded, and a second column would be one more thing to keep in step.
        var boundAt = await dbContext.TelegramBindTokens
            .AsNoTracking()
            .Where(t => t.UserId == actor.UserId && t.UsedAt != null)
            .OrderByDescending(t => t.UsedAt)
            .Select(t => t.UsedAt)
            .FirstOrDefaultAsync(cancellationToken);

        return new TelegramStatusDto(true, boundAt);
    }

    public async Task UnbindAsync(CancellationToken cancellationToken)
    {
        var actor = RequireActor();

        var user = await dbContext.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == actor.UserId, cancellationToken)
            ?? throw new NotFoundException();

        if (user.TelegramChatId is null)
        {
            return;
        }

        user.UnbindTelegram(clock.UtcNow);

        auditWriter.Write(new AuditEntry
        {
            Action = AuditActions.TelegramUnbind,
            EntityType = nameof(Domain.Users.User),
            EntityId = user.Id,
            BranchId = user.BranchId,
            CategoryId = user.CategoryId,
        });

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Redeems <c>/start &lt;token&gt;</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every failure answers with the same text. Telling «no such token» apart from «expired» or
    /// «already used» would confirm to whoever is guessing that a value was otherwise real, and the
    /// reply goes to a chat that has proven nothing about who owns it.
    /// </para>
    /// <para>
    /// The account bound is the token's owner (<c>TG-009</c>). Nothing from the Telegram payload
    /// chooses it.
    /// </para>
    /// </remarks>
    public async Task<string> RedeemBindTokenAsync(
        string chatId,
        string? plainToken,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        if (string.IsNullOrWhiteSpace(plainToken))
        {
            return BindFailureReply;
        }

        if (!WithinAttemptRate(chatId))
        {
            logger.LogWarning("Telegram bind attempts exceeded the hourly limit for a chat.");

            return "Слишком много попыток. Повторите через час.";
        }

        var candidate = await dbContext.TelegramBindTokens
            .Where(t => t.TokenHash == tokens.HashToken(plainToken))
            .FirstOrDefaultAsync(cancellationToken);

        // TG-012: the hash lookup narrows the row, and the comparison that decides is constant-time.
        if (candidate is null || !tokens.FixedTimeEquals(candidate.TokenHash, tokens.HashToken(plainToken)))
        {
            await RecordAttemptAsync(chatId, null, AuditResult.Failure, "token_invalid", now, cancellationToken);

            return BindFailureReply;
        }

        if (!candidate.IsRedeemable(now))
        {
            await RecordAttemptAsync(chatId, candidate.UserId, AuditResult.Failure, "token_expired", now, cancellationToken);

            return BindFailureReply;
        }

        // TG-008: one chat, one account. Otherwise two people sharing a device would silently receive
        // each other's notifications.
        var takenByAnother = await dbContext.Users
            .IgnoreQueryFilters()
            .AnyAsync(u => u.TelegramChatId == chatId && u.Id != candidate.UserId, cancellationToken);

        if (takenByAnother)
        {
            await RecordAttemptAsync(chatId, candidate.UserId, AuditResult.Failure, "chat_already_bound", now, cancellationToken);

            return "Этот Telegram-аккаунт уже привязан к другому пользователю.";
        }

        var user = await dbContext.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == candidate.UserId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            await RecordAttemptAsync(chatId, candidate.UserId, AuditResult.Failure, "user_unavailable", now, cancellationToken);

            return BindFailureReply;
        }

        // TG-007: the token is spent and the binding written in one transaction. Spending it without
        // binding would lock the person out until it expired.
        candidate.Redeem(now);
        user.BindTelegram(chatId, now);

        await RecordAttemptAsync(chatId, user.Id, AuditResult.Success, reason: null, now, cancellationToken);

        return "Готово. Уведомления MentorTaskFlow будут приходить сюда.";
    }

    private const string BindFailureReply =
        "Ссылка привязки недействительна или её срок истёк. Запросите новую в личном кабинете.";

    /// <summary>
    /// <c>TG-015</c>: both outcomes are recorded, and neither record contains the token.
    /// </summary>
    /// <remarks>
    /// System-written: the actor is a Telegram chat, not an authenticated principal, so there is no
    /// request context to attribute the entry to.
    /// </remarks>
    private async Task RecordAttemptAsync(
        string chatId,
        Guid? userId,
        AuditResult result,
        string? reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var scope = userId is { } id
            ? await dbContext.Users
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(u => u.Id == id)
                .Select(u => new { u.OrganizationId, u.BranchId })
                .FirstOrDefaultAsync(cancellationToken)
            : null;

        if (scope is null)
        {
            // An attempt with a token matching no user cannot be attributed to an organization, and
            // AuditLog.OrganizationId is mandatory always (TEN-041). The technical log carries it
            // instead; the counter of rejected attempts is what a review looks at.
            logger.LogWarning("Telegram bind attempt rejected before an account could be identified.");
            await dbContext.SaveChangesAsync(cancellationToken);

            return;
        }

        auditWriter.WriteSystem(
            new AuditEntry
            {
                Action = AuditActions.TelegramBind,
                EntityType = nameof(Domain.Users.User),
                EntityId = userId,
                Result = result,
                FailureReason = reason,

                // The chat identifier is deliberately absent: it is personal data of the recipient and
                // adds nothing a review of the binding needs (AUD-022).
                Metadata = JsonSerializer.SerializeToDocument(new { outcome = result.ToString() }),
            },
            scope.OrganizationId,
            scope.BranchId);

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary><c>TG-014</c>: 5 issues an hour per user.</summary>
    private async Task EnsureIssueRateAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var since = now.AddHours(-1);

        var issued = await dbContext.TelegramBindTokens
            .AsNoTracking()
            .CountAsync(t => t.UserId == userId && t.CreatedAt >= since, cancellationToken);

        if (issued >= _options.BindTokenRequestsPerHour)
        {
            throw new TooManyRequestsException("Слишком много запросов на привязку. Повторите позже.");
        }
    }

    /// <summary>
    /// <c>TG-014</c>: 20 redemption attempts an hour per chat.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Counted in memory, keyed by a hash of the chat identifier so the counter itself holds no
    /// personal data. The audit journal cannot serve as the counter: it deliberately does not record
    /// the chat, because that is the recipient's personal data and a review of a binding does not need
    /// it (<c>AUD-022</c>).
    /// </para>
    /// <para>
    /// The consequence, stated plainly: with several API replicas the budget is per process, so the
    /// effective limit is 20 × replicas. That is accepted — the limit is defence in depth against a
    /// 256-bit token, not the thing that makes guessing infeasible (<c>TG-011</c>).
    /// </para>
    /// </remarks>
    private bool WithinAttemptRate(string chatId)
    {
        var key = $"tg-bind-attempts:{tokens.HashToken(chatId)}";
        var attempts = cache.GetOrCreate(key, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
            return 0;
        });

        if (attempts >= _options.BindAttemptsPerHourPerChat)
        {
            return false;
        }

        cache.Set(key, attempts + 1, TimeSpan.FromHours(1));

        return true;
    }

    private ICurrentUserContext RequireActor() => currentUser.Current ?? throw new UnauthorizedException();
}
