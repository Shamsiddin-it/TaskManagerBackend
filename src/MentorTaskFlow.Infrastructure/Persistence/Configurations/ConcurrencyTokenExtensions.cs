using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MentorTaskFlow.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps PostgreSQL's system column <c>xmin</c> as the optimistic concurrency token (<c>DEPLOY-006</c>).
/// </summary>
/// <remarks>
/// <para>
/// No physical version column is created. SQL Server's <c>rowversion</c> of version 2.0 does not exist
/// in PostgreSQL, and adding a hand-maintained counter would put the burden of incrementing it on
/// every write path.
/// </para>
/// <para>
/// Accepted limitation: <c>xmin</c> changes on any physical row rewrite, including <c>VACUUM FULL</c>
/// and <c>CLUSTER</c>. Those run only in a maintenance window with the API stopped
/// (<c>DEPLOY-021</c>), so users never see a spurious conflict.
/// </para>
/// <para>
/// Append-only entities (Submission, Review, TaskEvent, AuditLog, the two history tables) have no
/// token: they are never updated.
/// </para>
/// </remarks>
internal static class ConcurrencyTokenExtensions
{
    public const string PropertyName = "ConcurrencyToken";

    public static EntityTypeBuilder<TEntity> ApplyConcurrencyToken<TEntity>(this EntityTypeBuilder<TEntity> builder)
        where TEntity : class
    {
        builder.Property<uint>(PropertyName)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        return builder;
    }
}
