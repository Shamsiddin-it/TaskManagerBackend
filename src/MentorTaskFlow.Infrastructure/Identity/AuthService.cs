using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Application.Common.Exceptions;
using MentorTaskFlow.Application.Common.Security;
using MentorTaskFlow.Contracts.Auth;
using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Domain.Auditing;
using MentorTaskFlow.Domain.Common;
using MentorTaskFlow.Domain.Identity;
using MentorTaskFlow.Domain.Users;
using MentorTaskFlow.Infrastructure.Options;
using MentorTaskFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MentorTaskFlow.Infrastructure.Identity;

/// <inheritdoc />
/// <remarks>
/// Every query here uses <c>IgnoreQueryFilters</c>, and that is correct rather than a shortcut:
/// authentication runs <b>before</b> a tenant scope exists — establishing it is the outcome of this
/// service, not its precondition. Each lookup is instead pinned to a single primary key, a globally
/// unique normalized email, or a token hash, none of which a caller can use to reach another
/// tenant's data.
/// </remarks>
public sealed class AuthService(
    MentorTaskFlowDbContext dbContext,
    IPasswordHasher passwordHasher,
    ISecureTokenService secureTokenService,
    IJwtTokenService jwtTokenService,
    ITokenVersionValidator tokenVersionValidator,
    PasswordPolicy passwordPolicy,
    IAuditWriter auditWriter,
    IOptions<AuthOptions> options,
    IClock clock) : IAuthService
{
    private readonly AuthOptions _options = options.Value;

    public async Task<AuthenticationResult> LoginAsync(LoginRequest request, string? ipAddress, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var normalizedEmail = Normalization.ToNormalized(request.Email ?? string.Empty);

        var user = await dbContext.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.NormalizedEmail == normalizedEmail, cancellationToken);

        if (user is null)
        {
            throw InvalidCredentials();
        }

        // USER-004 mandates a distinct code for a deactivated account. It does confirm the account
        // exists, but the TZ chooses that trade deliberately: the alternative leaves a locked-out
        // employee unable to tell «wrong password» from «access withdrawn».
        if (!user.IsActive)
        {
            throw new UnauthorizedException(ErrorCodes.UserDeactivated, "Учётная запись деактивирована.");
        }

        // Lockout and «password never set» both answer INVALID_CREDENTIALS. Saying «you are locked
        // out» would confirm the address belongs to a real account and hand an attacker a free
        // enumeration oracle (AUTH-024, USER-021).
        if (user.LockoutUntil is { } lockoutUntil && lockoutUntil > now)
        {
            throw InvalidCredentials();
        }

        if (user.PasswordHash is null || !passwordHasher.Verify(request.Password ?? string.Empty, user.PasswordHash))
        {
            user.RegisterFailedLogin(_options.LockoutAttempts, _options.LockoutDuration, now);
            await dbContext.SaveChangesAsync(cancellationToken);
            throw InvalidCredentials();
        }

        user.RegisterSuccessfulLogin(now);

        var refreshToken = IssueRefreshToken(user, familyId: null, ipAddress, now);
        await dbContext.SaveChangesAsync(cancellationToken);

        return await BuildResultAsync(user, refreshToken, cancellationToken);
    }

    public async Task<AuthenticationResult> RefreshAsync(string refreshToken, string? ipAddress, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw RefreshTokenInvalid();
        }

        var tokenHash = secureTokenService.HashToken(refreshToken);

        // Revoked rows are deliberately included in the lookup: filtering them out in SQL would make a
        // replayed token indistinguishable from an unknown one, and reuse detection would never fire.
        var stored = await dbContext.RefreshTokens
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);

        if (stored is null)
        {
            throw RefreshTokenInvalid();
        }

        var user = await dbContext.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == stored.UserId, cancellationToken)
            ?? throw RefreshTokenInvalid();

        if (stored.RevokedAt is not null)
        {
            await HandleReuseDetectionAsync(stored, user, ipAddress, now, cancellationToken);
        }

        if (!stored.IsActive(now))
        {
            throw RefreshTokenInvalid();
        }

        if (!user.IsActive)
        {
            throw new UnauthorizedException(ErrorCodes.UserDeactivated, "Учётная запись деактивирована.");
        }

        var replacement = IssueRefreshToken(user, stored.FamilyId, ipAddress, now);
        stored.Rotate(replacement.Token.Id, ipAddress, now);

        await dbContext.SaveChangesAsync(cancellationToken);

        return await BuildResultAsync(user, replacement, cancellationToken);
    }

    public async Task LogoutAsync(string refreshToken, string? ipAddress, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return;
        }

        var tokenHash = secureTokenService.HashToken(refreshToken);

        var stored = await dbContext.RefreshTokens
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);

        // Silent on an unknown or already-revoked token. Logout is not an authentication decision, and
        // reporting «no such session» would let an unauthenticated caller probe which tokens exist.
        if (stored is null)
        {
            return;
        }

        stored.Revoke(RefreshTokenRevocationReason.Logout, ipAddress, now);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<AuthUserDto> GetProfileAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await dbContext.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
            ?? throw new UnauthorizedException();

        return await MapProfileAsync(user, cancellationToken);
    }

    public async Task<AuthenticationResult> ChangePasswordAsync(
        Guid userId,
        ChangePasswordRequest request,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        var user = await dbContext.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
            ?? throw new UnauthorizedException();

        if (user.PasswordHash is null || !passwordHasher.Verify(request.CurrentPassword ?? string.Empty, user.PasswordHash))
        {
            throw new ValidationAppException("currentPassword", "Текущий пароль указан неверно.");
        }

        passwordPolicy.Validate(request.NewPassword);

        // SetPasswordHash increments TokenVersion, which is what actually invalidates outstanding
        // access tokens (AUTH-014).
        user.SetPasswordHash(passwordHasher.Hash(request.NewPassword), now);

        await RevokeAllRefreshTokensAsync(user.Id, RefreshTokenRevocationReason.PasswordChanged, ipAddress, now, cancellationToken);

        // The caller keeps working: a fresh pair is issued in the same response, so securing the
        // account does not sign the person out of the tab they are sitting in (AUTH-014).
        var refreshToken = IssueRefreshToken(user, familyId: null, ipAddress, now);

        await dbContext.SaveChangesAsync(cancellationToken);
        tokenVersionValidator.Invalidate(user.Id);

        return await BuildResultAsync(user, refreshToken, cancellationToken);
    }

    public async Task<string?> ForgotPasswordAsync(ForgotPasswordRequest request, string? ipAddress, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var normalizedEmail = Normalization.ToNormalized(request.Email ?? string.Empty);

        var user = await dbContext.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.NormalizedEmail == normalizedEmail, cancellationToken);

        // No account, or a deactivated one: return nothing and let the caller answer 202 all the same.
        // The response must not differ in body, status or timing class from the success path
        // (AUTH-015).
        if (user is null || !user.IsActive)
        {
            return null;
        }

        var (plainToken, _) = await IssueSecurityTokenAsync(
            user,
            SecurityTokenPurpose.ResetPassword,
            _options.ResetPasswordTokenLifetime,
            ipAddress,
            now,
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);

        return BuildPasswordLink("reset-password", plainToken);
    }

    public Task ResetPasswordAsync(ResetPasswordRequest request, string? ipAddress, CancellationToken cancellationToken) =>
        ApplyTokenPasswordAsync(
            request.Token,
            request.NewPassword,
            SecurityTokenPurpose.ResetPassword,
            RefreshTokenRevocationReason.PasswordChanged,
            ipAddress,
            cancellationToken);

    public Task SetPasswordAsync(SetPasswordRequest request, string? ipAddress, CancellationToken cancellationToken) =>
        ApplyTokenPasswordAsync(
            request.Token,
            request.NewPassword,
            SecurityTokenPurpose.SetPassword,
            RefreshTokenRevocationReason.PasswordChanged,
            ipAddress,
            cancellationToken);

    // -----------------------------------------------------------------
    // Internals
    // -----------------------------------------------------------------

    private async Task ApplyTokenPasswordAsync(
        string? plainToken,
        string? newPassword,
        SecurityTokenPurpose purpose,
        RefreshTokenRevocationReason revocationReason,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        if (string.IsNullOrWhiteSpace(plainToken))
        {
            throw SecurityTokenInvalid();
        }

        var tokenHash = secureTokenService.HashToken(plainToken);

        var securityToken = await dbContext.UserSecurityTokens
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash && t.Purpose == purpose, cancellationToken);

        // The password is validated only after the token proves usable. Reporting policy violations to
        // a holder of an invalid token would let anyone probe the password rules and, worse, confirm
        // that a guessed token was otherwise fine.
        if (securityToken is null || !securityToken.TryRedeem(now))
        {
            throw SecurityTokenInvalid();
        }

        var user = await dbContext.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == securityToken.UserId, cancellationToken)
            ?? throw SecurityTokenInvalid();

        if (!user.IsActive)
        {
            throw SecurityTokenInvalid();
        }

        passwordPolicy.Validate(newPassword);
        user.SetPasswordHash(passwordHasher.Hash(newPassword!), now);

        await RevokeAllRefreshTokensAsync(user.Id, revocationReason, ipAddress, now, cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
        tokenVersionValidator.Invalidate(user.Id);
    }

    /// <summary>
    /// A revoked token was presented, so the value leaked. The whole family goes, and
    /// <c>TokenVersion</c> is incremented so outstanding access tokens die too (<c>AUTH-008</c>).
    /// </summary>
    /// <remarks>
    /// The legitimate holder and the attacker are indistinguishable at this point — either could be
    /// the one replaying — so ending both sessions is the only safe answer. The AuditLog entry
    /// (<c>auth.refresh_reuse_detected</c>) is added in Phase 3 with the audit module.
    /// </remarks>
    private async Task HandleReuseDetectionAsync(
        RefreshToken presented,
        User user,
        string? ipAddress,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var family = await dbContext.RefreshTokens
            .Where(t => t.UserId == presented.UserId && t.FamilyId == presented.FamilyId)
            .ToListAsync(cancellationToken);

        foreach (var token in family)
        {
            token.Revoke(RefreshTokenRevocationReason.ReuseDetected, ipAddress, now);
        }

        user.BumpTokenVersion(now);

        // Recorded as a system action: the caller has not proven who they are — that is the whole
        // point of the detection — so attributing it to the token's owner would name a probable
        // victim as the actor (AUTH-008, AUD-003).
        auditWriter.WriteSystem(
            new AuditEntry
            {
                Action = AuditActions.AuthRefreshReuseDetected,
                EntityType = nameof(User),
                EntityId = user.Id,
                Result = AuditResult.Failure,
                FailureReason = ErrorCodes.RefreshTokenReuseDetected,
            },
            user.OrganizationId,
            user.BranchId);

        await dbContext.SaveChangesAsync(cancellationToken);
        tokenVersionValidator.Invalidate(user.Id);

        throw new UnauthorizedException(
            ErrorCodes.RefreshTokenReuseDetected,
            "Обнаружено повторное использование refresh-токена. Все сессии завершены.");
    }

    private async Task RevokeAllRefreshTokensAsync(
        Guid userId,
        RefreshTokenRevocationReason reason,
        string? ipAddress,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var tokens = await dbContext.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var token in tokens)
        {
            token.Revoke(reason, ipAddress, now);
        }
    }

    private IssuedRefreshToken IssueRefreshToken(User user, Guid? familyId, string? ipAddress, DateTimeOffset now)
    {
        var (plainToken, tokenHash) = secureTokenService.Generate();

        var token = familyId is { } family
            ? RefreshToken.IssueInFamily(user.Id, tokenHash, family, _options.RefreshTokenLifetime, ipAddress, now)
            : RefreshToken.IssueNewFamily(user.Id, tokenHash, _options.RefreshTokenLifetime, ipAddress, now);

        dbContext.RefreshTokens.Add(token);

        return new IssuedRefreshToken(token, plainToken);
    }

    private async Task<(string PlainToken, UserSecurityToken Token)> IssueSecurityTokenAsync(
        User user,
        SecurityTokenPurpose purpose,
        TimeSpan lifetime,
        string? ipAddress,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Exactly one live link per purpose (AUTH-017): the previous one is retired before the new one
        // is added, or the partial unique index would reject the insert.
        var existing = await dbContext.UserSecurityTokens
            .Where(t => t.UserId == user.Id && t.Purpose == purpose && t.UsedAt == null && t.InvalidatedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var token in existing)
        {
            token.Invalidate(now);
        }

        var (plainToken, tokenHash) = secureTokenService.Generate();
        var securityToken = UserSecurityToken.Issue(user.Id, purpose, tokenHash, lifetime, ipAddress, now);
        dbContext.UserSecurityTokens.Add(securityToken);

        return (plainToken, securityToken);
    }

    /// <summary>Issues a set-password link for a freshly created account (<c>AUTH-020</c>).</summary>
    internal async Task<string> IssueSetPasswordLinkAsync(User user, string? ipAddress, CancellationToken cancellationToken)
    {
        var (plainToken, _) = await IssueSecurityTokenAsync(
            user,
            SecurityTokenPurpose.SetPassword,
            _options.SetPasswordTokenLifetime,
            ipAddress,
            clock.UtcNow,
            cancellationToken);

        return BuildPasswordLink("set-password", plainToken);
    }

    private string BuildPasswordLink(string path, string plainToken) =>
        $"{_options.AppBaseUrl.TrimEnd('/')}/{path}?token={Uri.EscapeDataString(plainToken)}";

    private async Task<AuthenticationResult> BuildResultAsync(
        User user,
        IssuedRefreshToken refreshToken,
        CancellationToken cancellationToken)
    {
        var accessToken = jwtTokenService.Issue(user);
        var profile = await MapProfileAsync(user, cancellationToken);

        var response = new LoginResponse(
            profile,
            accessToken.Value,
            accessToken.ExpiresAt,
            TelegramBound: user.TelegramChatId is not null);

        return new AuthenticationResult(response, refreshToken.PlainToken, refreshToken.Token.ExpiresAt);
    }

    private async Task<AuthUserDto> MapProfileAsync(User user, CancellationToken cancellationToken)
    {
        var organization = await dbContext.Organizations
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(o => o.Id == user.OrganizationId)
            .Select(o => new OrganizationSummaryDto(o.Id, o.Name))
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                $"User {user.Id} references organization {user.OrganizationId}, which does not exist.");

        BranchSummaryDto? branch = null;

        if (user.BranchId is { } branchId)
        {
            branch = await dbContext.Branches
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(b => b.Id == branchId)
                .Select(b => new BranchSummaryDto(b.Id, b.Name, b.Code, b.IsHeadOffice))
                .FirstOrDefaultAsync(cancellationToken);
        }

        return new AuthUserDto(
            user.Id,
            user.FullName,
            user.Email,
            user.Role.ToString(),
            user.AdminScope?.ToString(),
            organization,
            branch,
            user.CategoryId);
    }

    private static UnauthorizedException InvalidCredentials() =>
        new(ErrorCodes.InvalidCredentials, "Неверный email или пароль.");

    private static UnauthorizedException RefreshTokenInvalid() =>
        new(ErrorCodes.RefreshTokenInvalid, "Refresh-токен недействителен.");

    private static SecurityTokenInvalidException SecurityTokenInvalid() => new();

    private sealed record IssuedRefreshToken(RefreshToken Token, string PlainToken);
}
