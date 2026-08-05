using MentorTaskFlow.Application.Common.Security;
using MentorTaskFlow.Infrastructure.Options;
using MentorTaskFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace MentorTaskFlow.Infrastructure.Identity;

/// <summary>
/// Compares the <c>tv</c> claim against the stored value, with a short-lived cache (<c>AUTH-028</c>).
/// </summary>
/// <remarks>
/// <para>
/// At 100 concurrent users and a 30-second TTL this costs at most ~200 primary-key lookups per
/// minute — under 1% of the target load profile of TZ 28, so no extra infrastructure is warranted.
/// Beyond two API instances the TZ recommends swapping <see cref="IMemoryCache"/> for a distributed
/// cache; the contract here does not change when that happens (<c>AUTH-028</c>).
/// </para>
/// <para>
/// Deactivation is reported separately from a version mismatch so the caller can answer 401
/// <c>USER_DEACTIVATED</c> instead of the generic mismatch (<c>USER-004</c>). That distinction is safe:
/// the caller already proved possession of a validly signed token for that account, so nothing is
/// disclosed that they did not already know.
/// </para>
/// </remarks>
public sealed class TokenVersionValidator(
    MentorTaskFlowDbContext dbContext,
    IMemoryCache cache,
    IOptions<AuthOptions> options) : ITokenVersionValidator
{
    private readonly AuthOptions _options = options.Value;

    public async Task<TokenVersionCheck> CheckAsync(Guid userId, int presentedTokenVersion, CancellationToken cancellationToken)
    {
        var snapshot = await GetSnapshotAsync(userId, cancellationToken);

        if (snapshot is null || snapshot.TokenVersion != presentedTokenVersion)
        {
            return TokenVersionCheck.Mismatch;
        }

        return snapshot.IsActive ? TokenVersionCheck.Valid : TokenVersionCheck.Deactivated;
    }

    public void Invalidate(Guid userId) => cache.Remove(CacheKey(userId));

    private async Task<UserSecuritySnapshot?> GetSnapshotAsync(Guid userId, CancellationToken cancellationToken)
    {
        var key = CacheKey(userId);

        if (cache.TryGetValue<UserSecuritySnapshot>(key, out var cached))
        {
            return cached;
        }

        // IgnoreQueryFilters is required and safe: this runs before the tenant scope has been
        // established for the request — establishing it is what this check gates — and the lookup is
        // pinned to a single primary key supplied by a signed token, not by user input.
        var snapshot = await dbContext.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new UserSecuritySnapshot(u.TokenVersion, u.IsActive))
            .FirstOrDefaultAsync(cancellationToken);

        if (snapshot is not null)
        {
            cache.Set(key, snapshot, TimeSpan.FromSeconds(_options.TokenVersionCacheSeconds));
        }

        return snapshot;
    }

    private static string CacheKey(Guid userId) => $"token-version:{userId}";

    private sealed record UserSecuritySnapshot(int TokenVersion, bool IsActive);
}
