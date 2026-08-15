using MentorTaskFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace MentorTaskFlow.Infrastructure.Observability;

/// <summary>
/// Turns tenant identifiers into the label values <c>OBS-011</c> permits.
/// </summary>
/// <remarks>
/// <para>
/// <c>OBS-011</c> names <c>Organization.Slug</c> and <c>Branch.Code</c>, not their UUIDs — and it is
/// right to: a slug is short, stable and readable in an alert, whereas a UUID makes an on-call
/// engineer open a database before they can tell which branch is failing.
/// </para>
/// <para>
/// The values are cached because the counters that need them sit on request paths — a rejected scope
/// override, a missing branch header — and a database round trip per rejected request would turn a
/// metric into a denial-of-service amplifier. A slug is immutable (<c>ORG-020</c>) and a branch code
/// changes about never, so a long window costs nothing.
/// </para>
/// <para>
/// An unknown id resolves to <see cref="Unknown"/> rather than to the id itself. Falling back to the
/// UUID would quietly reintroduce exactly the label <c>OBS-011</c> forbids, and would do it precisely
/// when something is already wrong.
/// </para>
/// </remarks>
public sealed class TenantLabelResolver(IServiceScopeFactory scopeFactory, IMemoryCache cache)
{
    public const string Unknown = "unknown";

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(30);

    public string Organization(Guid? organizationId) =>
        organizationId is { } id
            ? Resolve($"metrics:org:{id}", context => context.Organizations
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(o => o.Id == id)
                .Select(o => o.Slug))
            : Unknown;

    public string Branch(Guid? branchId) =>
        branchId is { } id
            ? Resolve($"metrics:branch:{id}", context => context.Branches
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(b => b.Id == id)
                .Select(b => b.Code))
            : Unknown;

    /// <summary>
    /// Reads one value synchronously, from cache where possible.
    /// </summary>
    /// <remarks>
    /// Synchronous by necessity: the callers are metric recordings inside exception filters and
    /// middleware, where there is no place to await. The blocking call happens at most once per id per
    /// window, and never on the path that is already succeeding.
    /// </remarks>
    private string Resolve(string key, Func<MentorTaskFlowDbContext, IQueryable<string>> query)
    {
        if (cache.TryGetValue(key, out string? cached) && cached is not null)
        {
            return cached;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<MentorTaskFlowDbContext>();

            var value = query(context).FirstOrDefault() ?? Unknown;

            cache.Set(key, value, Window);

            return value;
        }
        catch (Exception)
        {
            // A metric label is never worth failing a request for. The database being unreachable is
            // already reported by the readiness probe; losing a label here adds nothing to that.
            return Unknown;
        }
    }
}
