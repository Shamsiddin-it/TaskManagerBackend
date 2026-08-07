namespace MentorTaskFlow.Application.Common.Abstractions;

/// <summary>
/// Commits the work accumulated during one request.
/// </summary>
/// <remarks>
/// <para>
/// Exists so a controller can commit without touching <c>DbContext</c>, which
/// <c>SEC-031</c> confines to Infrastructure and <c>TenantScopeTests</c> enforces. Writers such as
/// <see cref="IAuditWriter"/> and <see cref="IOutboxWriter"/> only stage rows; something has to commit
/// them, and it must be the same transaction as the action being recorded.
/// </para>
/// <para>
/// Intentionally minimal. Explicit transaction control belongs to the services that need it — the
/// branch-transfer and head-office operations, for instance — not to a generic wrapper that would
/// invite nesting.
/// </para>
/// </remarks>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
