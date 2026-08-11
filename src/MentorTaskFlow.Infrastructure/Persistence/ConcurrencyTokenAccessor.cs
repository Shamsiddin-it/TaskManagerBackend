using MentorTaskFlow.Application.Common.Concurrency;
using MentorTaskFlow.Application.Common.Exceptions;
using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace MentorTaskFlow.Infrastructure.Persistence;

/// <summary>
/// Reads and applies the <c>xmin</c> shadow property that backs optimistic concurrency (TZ 11.6).
/// </summary>
/// <remarks>
/// <c>xmin</c> is a shadow property, so it is reachable only through the change tracker. Centralising
/// that here keeps every service from repeating the <c>Entry(...).Property(...)</c> incantation and,
/// more importantly, keeps them from forgetting the original-value assignment that makes the check
/// actually happen.
/// </remarks>
public static class ConcurrencyTokenAccessor
{
    /// <summary>Current token of a tracked entity, for returning to the client.</summary>
    public static string Read<TEntity>(this DbContext dbContext, TEntity entity)
        where TEntity : class =>
        ConcurrencyToken.Encode(dbContext.Entry(entity).Property<uint>(ConcurrencyTokenExtensions.PropertyName).CurrentValue);

    /// <summary>
    /// Arms the concurrency check with the value the client presented.
    /// </summary>
    /// <remarks>
    /// The <b>original</b> value is what EF Core puts in the <c>WHERE</c> clause of the UPDATE. Setting
    /// the current value instead would produce an update that always matches, and the conflict would
    /// go undetected — the classic way optimistic concurrency silently stops working.
    /// </remarks>
    public static void Expect<TEntity>(this DbContext dbContext, TEntity entity, string? clientToken)
        where TEntity : class
    {
        var expected = ConcurrencyToken.Decode(clientToken);

        dbContext.Entry(entity)
            .Property<uint>(ConcurrencyTokenExtensions.PropertyName)
            .OriginalValue = expected;
    }
}

/// <summary>
/// Turns <see cref="DbUpdateConcurrencyException"/> into 409 <c>CONCURRENCY_CONFLICT</c> carrying the
/// current token (<c>API-026</c>).
/// </summary>
/// <remarks>
/// The response includes <c>currentConcurrencyToken</c> so the client can offer a reload without a
/// second round trip — the whole reason the TZ specifies it rather than a bare 409.
/// </remarks>
public static class ConcurrencyConflict
{
    public static async Task SaveWithConcurrencyCheckAsync<TEntity>(
        this MentorTaskFlowDbContext dbContext,
        TEntity entity,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Reload from the database rather than trusting the in-memory entity: the row was changed
            // by somebody else, and the token the client needs is theirs, not ours.
            dbContext.ChangeTracker.Clear();

            var current = await dbContext.FindAsync<TEntity>([GetKey(dbContext, entity)], cancellationToken);

            var details = new Dictionary<string, object?>();

            if (current is not null)
            {
                details["currentConcurrencyToken"] = dbContext.Read(current);
            }

            throw new ConflictException(
                ErrorCodes.ConcurrencyConflict,
                "Объект был изменён другим пользователем. Перезагрузите данные и повторите операцию.",
                details);
        }
    }

    private static object GetKey<TEntity>(MentorTaskFlowDbContext dbContext, TEntity entity)
        where TEntity : class
    {
        var keyProperty = dbContext.Entry(entity).Metadata.FindPrimaryKey()!.Properties[0];
        return dbContext.Entry(entity).Property(keyProperty.Name).CurrentValue!;
    }
}
