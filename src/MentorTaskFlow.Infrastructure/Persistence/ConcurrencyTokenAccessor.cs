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
    /// <summary>
    /// Current token of a <b>tracked</b> entity, for returning to the client.
    /// </summary>
    /// <remarks>
    /// Tracked only. Calling <c>Entry()</c> on an entity loaded with <c>AsNoTracking</c> starts
    /// tracking it afresh with default shadow values, so the token would encode 0 and the client's
    /// next write would be refused as a conflict on its first attempt. Read paths must project
    /// <see cref="EncodeFrom"/> from the query instead.
    /// </remarks>
    public static string Read<TEntity>(this DbContext dbContext, TEntity entity)
        where TEntity : class
    {
        var entry = dbContext.Entry(entity);

        if (entry.State is EntityState.Detached)
        {
            throw new InvalidOperationException(
                $"{typeof(TEntity).Name} is not tracked, so its concurrency token cannot be read from the change " +
                "tracker. Project EF.Property<uint>(e, \"ConcurrencyToken\") in the query instead.");
        }

        return ConcurrencyToken.Encode(entry.Property<uint>(ConcurrencyTokenExtensions.PropertyName).CurrentValue);
    }

    /// <summary>Encodes an <c>xmin</c> read directly by a projection, for no-tracking reads.</summary>
    public static string EncodeFrom(uint xmin) => ConcurrencyToken.Encode(xmin);

    /// <summary>
    /// Arms the concurrency check with the value the client presented, and rejects a token that is
    /// already known to be stale.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <b>original</b> value is what EF Core puts in the <c>WHERE</c> clause of the UPDATE. Setting
    /// the current value instead would produce an update that always matches, and the conflict would
    /// go undetected — the classic way optimistic concurrency silently stops working.
    /// </para>
    /// <para>
    /// The eager comparison decides precedence. Left to <c>SaveChanges</c>, the check runs after the
    /// domain transition, so a client replaying a stale request is told its transition is illegal
    /// rather than that its copy is out of date — and <c>FE-011</c>'s reload dialog never appears,
    /// leaving the client holding the stale copy that caused the problem. <c>ASN-007</c> makes a
    /// mismatched token a conflict on its own, whatever else is wrong with the request. EF's check
    /// stays armed for the race between this comparison and the write.
    /// </para>
    /// </remarks>
    public static void Expect<TEntity>(this DbContext dbContext, TEntity entity, string? clientToken)
        where TEntity : class
    {
        var expected = ConcurrencyToken.Decode(clientToken);
        var property = dbContext.Entry(entity).Property<uint>(ConcurrencyTokenExtensions.PropertyName);

        if (property.CurrentValue != expected)
        {
            // The row in hand is the current one, so its token can be handed back directly (API-026).
            throw new ConflictException(
                ErrorCodes.ConcurrencyConflict,
                "Объект был изменён другим пользователем. Перезагрузите данные и повторите операцию.",
                new Dictionary<string, object?>
                {
                    ["currentConcurrencyToken"] = ConcurrencyToken.Encode(property.CurrentValue),
                });
        }

        property.OriginalValue = expected;
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
