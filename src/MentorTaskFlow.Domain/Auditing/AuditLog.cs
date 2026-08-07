using System.Text.Json;
using MentorTaskFlow.Domain.Common;
using MentorTaskFlow.Domain.Tenancy;

namespace MentorTaskFlow.Domain.Auditing;

/// <summary>Whether an action was taken by a person or by a background task (TZ 10.14).</summary>
public enum AuditActorType
{
    User = 0,
    System = 1,
}

public enum AuditResult
{
    Success = 0,
    Failure = 1,
}

/// <summary>
/// Append-only record of an administrative or system action (TZ 10.14).
/// </summary>
/// <remarks>
/// <para>
/// One of four independent layers (TZ 27.1) that do not substitute for one another: TaskEvent is the
/// business history of a single assignment, AuditLog covers administrative and system actions, the
/// technical log is diagnostics with a 30-day life, and metrics are aggregates. A business event that
/// reaches only the technical log is a defect — that log is neither a source of truth nor kept long
/// enough (<c>AUD-005</c>).
/// </para>
/// <para>
/// <c>UPDATE</c> and <c>DELETE</c> are revoked from the application database role, so append-only is a
/// database guarantee rather than a coding convention (<c>AUD-001</c>, TZ 12.6).
/// </para>
/// </remarks>
public sealed class AuditLog : BaseEntity
{
    public const int ActionMaxLength = 64;
    public const int EntityTypeMaxLength = 64;
    public const int PathMaxLength = 256;
    public const int UserAgentMaxLength = 256;
    public const int FailureReasonMaxLength = 256;

    /// <summary>
    /// Always required. Records without an organization do not exist: even a background task runs in
    /// the context of a specific tenant (<c>AUD-021</c>).
    /// </summary>
    public Guid OrganizationId { get; private set; }

    /// <summary>
    /// Null only for the organization-level actions of <see cref="AuditActions.OrganizationLevelActions"/>
    /// (<c>TEN-048</c>); mandatory for everything else, enforced by <c>ck_audit_logs_branch_scope</c>.
    /// </summary>
    public Guid? BranchId { get; private set; }

    public Guid? CategoryId { get; private set; }

    /// <summary>Null for background tasks, where <see cref="ActorType"/> is <c>System</c>.</summary>
    public Guid? ActorId { get; private set; }

    public AuditActorType ActorType { get; private set; }

    /// <summary>The actor's role <b>at the time of the action</b>, not their role today.</summary>
    public UserRole? ActorRole { get; private set; }

    /// <summary>The actor's administrative contour at the time of the action (added in 2.2).</summary>
    public AdminScope? ActorAdminScope { get; private set; }

    public string Action { get; private set; } = null!;

    public string EntityType { get; private set; } = null!;

    public Guid? EntityId { get; private set; }

    public string? HttpMethod { get; private set; }

    /// <summary>Stored without the query string, which may carry tokens or signatures (<c>SEC-024</c>).</summary>
    public string? Path { get; private set; }

    /// <summary>Personal data; nulled by retention after 90 days while the record itself survives.</summary>
    public string? IpAddress { get; private set; }

    public string? UserAgent { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public AuditResult Result { get; private set; }

    /// <summary>An error code, never a sensitive value.</summary>
    public string? FailureReason { get; private set; }

    /// <summary>Ties this row to the API request, the TaskEvent and any notifications it produced.</summary>
    public Guid CorrelationId { get; private set; }

    /// <summary>Redacted values only (TZ 27.4).</summary>
    public JsonDocument? Metadata { get; private set; }

    public int MetadataSchemaVersion { get; private set; }

    private AuditLog()
    {
    }

    /// <summary>
    /// Records an action.
    /// </summary>
    /// <remarks>
    /// <paramref name="branchId"/> is validated against <see cref="AuditActions.OrganizationLevelActions"/>
    /// here as well as by the CHECK constraint. Application validation is never the only guard for an
    /// invariant a constraint can express (<c>TEN-023</c>), but catching it in the domain turns a
    /// database error at commit time into a clear failure at the call site.
    /// </remarks>
    public static AuditLog Record(
        string action,
        string entityType,
        Guid organizationId,
        Guid? branchId,
        Guid? categoryId,
        Guid? entityId,
        AuditActorType actorType,
        Guid? actorId,
        UserRole? actorRole,
        AdminScope? actorAdminScope,
        AuditResult result,
        Guid correlationId,
        DateTimeOffset now,
        string? httpMethod = null,
        string? path = null,
        string? ipAddress = null,
        string? userAgent = null,
        string? failureReason = null,
        JsonDocument? metadata = null)
    {
        if (organizationId == Guid.Empty)
        {
            throw new DomainException(
                DomainErrorCodes.ValidationFailed,
                "AuditLog.OrganizationId обязателен всегда (AUD-021).");
        }

        if (string.IsNullOrWhiteSpace(action) || action.Length > ActionMaxLength)
        {
            throw new DomainException(DomainErrorCodes.ValidationFailed, "AuditLog.Action обязателен.");
        }

        if (branchId is null && !AuditActions.OrganizationLevelActions.Contains(action))
        {
            throw new DomainException(
                DomainErrorCodes.ValidationFailed,
                $"BranchId обязателен для действия '{action}': оно не входит в перечень Organization-level (TEN-048).");
        }

        if (actorType is AuditActorType.System && actorId is not null)
        {
            throw new DomainException(
                DomainErrorCodes.ValidationFailed,
                "Системное действие не может иметь ActorId (10.14).");
        }

        return new AuditLog
        {
            Action = action,
            EntityType = entityType,
            OrganizationId = organizationId,
            BranchId = branchId,
            CategoryId = categoryId,
            EntityId = entityId,
            ActorType = actorType,
            ActorId = actorId,
            ActorRole = actorRole,
            ActorAdminScope = actorAdminScope,
            Result = result,
            CorrelationId = correlationId,
            OccurredAt = now,
            CreatedAt = now,
            HttpMethod = httpMethod,
            Path = Truncate(path, PathMaxLength),
            IpAddress = ipAddress,
            UserAgent = Truncate(userAgent, UserAgentMaxLength),
            FailureReason = Truncate(failureReason, FailureReasonMaxLength),
            Metadata = metadata,
            MetadataSchemaVersion = 1,
        };
    }

    /// <summary>Retention: address and user agent are dropped after 90 days (TZ 27.5).</summary>
    public void ForgetRequestOrigin()
    {
        IpAddress = null;
        UserAgent = null;
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}
