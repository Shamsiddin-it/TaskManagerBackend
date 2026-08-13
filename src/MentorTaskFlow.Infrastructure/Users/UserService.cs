using System.Text.Json;
using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Application.Common.Exceptions;
using MentorTaskFlow.Application.Common.Security;
using MentorTaskFlow.Application.Common.Tenancy;
using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Contracts.Tenancy;
using MentorTaskFlow.Contracts.Users;
using MentorTaskFlow.Domain.Auditing;
using MentorTaskFlow.Domain.Identity;
using MentorTaskFlow.Domain.Notifications;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Domain.Users;
using MentorTaskFlow.Infrastructure.Identity;
using MentorTaskFlow.Infrastructure.Persistence;
using MentorTaskFlow.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MentorTaskFlow.Infrastructure.Users;

/// <inheritdoc />
public sealed class UserService(
    MentorTaskFlowDbContext dbContext,
    ICurrentUserAccessor currentUser,
    IBranchContext branchContext,
    ITenantStateGuard tenantState,
    IAuditWriter auditWriter,
    IOutboxWriter outboxWriter,
    ITokenVersionValidator tokenVersionValidator,
    AuthService authService,
    IClock clock) : IUserService
{
    private static readonly string[] SortableColumns = ["fullName", "email", "createdAt"];

    public async Task<PagedResult<UserDto>> ListAsync(UserListQuery query, CancellationToken cancellationToken)
    {
        var actor = RequireActor();

        // USER-010: a Mentor has no access to the user list. They may not see the personal data or
        // individual results of other mentors, not even in their own category.
        if (actor.Role is UserRole.Mentor)
        {
            throw new ForbiddenException(ErrorCodes.Forbidden, "Список пользователей недоступен.");
        }

        var page = Math.Max(query.Page, PaginationLimits.DefaultPage);
        var pageSize = Math.Clamp(query.PageSize, PaginationLimits.MinPageSize, PaginationLimits.MaxPageSize);

        var source = ApplyVisibility(
            dbContext.Users.AsNoTracking().Where(u => u.OrganizationId == branchContext.EffectiveOrganizationId),
            actor);

        if (!string.IsNullOrWhiteSpace(query.Role) && Enum.TryParse<UserRole>(query.Role, out var role))
        {
            source = source.Where(u => u.Role == role);
        }

        if (query.CategoryId is { } categoryId)
        {
            source = source.Where(u => u.CategoryId == categoryId);
        }

        if (query.IsActive is { } isActive)
        {
            source = source.Where(u => u.IsActive == isActive);
        }

        source = ApplySort(source, query);

        // Computed under the same predicate as the rows, so the counter cannot disclose users outside
        // the caller's scope (TEN-002, API-012).
        var totalCount = await source.CountAsync(cancellationToken);

        var rows = await source
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(u => new UserRow(u, EF.Property<uint>(u, ConcurrencyTokenExtensions.PropertyName)))
            .ToListAsync(cancellationToken);

        var branches = branchContext.IsAllBranchesReadContext
            ? await LoadBranchSummariesAsync(rows.Select(r => r.User.BranchId), cancellationToken)
            : [];

        var items = rows
            .Select(row => ToDto(row.User, ConcurrencyTokenAccessor.EncodeFrom(row.Xmin), BranchOf(branches, row.User)))
            .ToList();

        return new PagedResult<UserDto>(items, page, pageSize, totalCount);
    }

    public async Task<UserDto> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        var actor = RequireActor();

        if (actor.Role is UserRole.Mentor)
        {
            throw new ForbiddenException(ErrorCodes.Forbidden, "Просмотр пользователей недоступен.");
        }

        var row = await ApplyVisibility(
                dbContext.Users
                    .AsNoTracking()
                    .Where(u => u.Id == userId && u.OrganizationId == branchContext.EffectiveOrganizationId),
                actor)
            .Select(u => new UserRow(u, EF.Property<uint>(u, ConcurrencyTokenExtensions.PropertyName)))
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException();

        return ToDto(row.User, ConcurrencyTokenAccessor.EncodeFrom(row.Xmin), null);
    }

    public async Task<UserDto> CreateAsync(CreateUserRequest request, CancellationToken cancellationToken)
    {
        var actor = RequireActor();

        if (!Enum.TryParse<UserRole>(request.Role, ignoreCase: false, out var targetRole))
        {
            throw new ValidationAppException("role", "Роль должна быть одной из: Admin, Lead, Mentor.");
        }

        var adminScope = ParseAdminScope(request, targetRole);
        await EnsureMayCreateAsync(actor, targetRole, adminScope, cancellationToken);

        var user = await BuildNewUserAsync(actor, request, targetRole, adminScope, cancellationToken);
        dbContext.Users.Add(user);

        await IssueInvitationAsync(user, cancellationToken);

        auditWriter.Write(new AuditEntry
        {
            // TEN-048: creating an Organization Admin is an organization-level act and carries no
            // branch; everything else is recorded inside the branch it happened in.
            Action = adminScope is AdminScope.Organization
                ? AuditActions.UserCreateOrganizationAdmin
                : AuditActions.UserCreate,
            EntityType = nameof(User),
            EntityId = user.Id,
            BranchId = user.BranchId,
            CategoryId = user.CategoryId,
            Metadata = JsonSerializer.SerializeToDocument(new
            {
                role = user.Role.ToString(),
                adminScope = user.AdminScope?.ToString(),
            }),
        });

        await SaveTranslatingConstraintsAsync(cancellationToken);

        return ToDto(user, dbContext.Read(user), null);
    }

    public async Task<UserDto> PatchAsync(Guid userId, PatchUserRequest request, CancellationToken cancellationToken)
    {
        var user = await FindManageableAsync(userId, cancellationToken);
        dbContext.Expect(user, request.ConcurrencyToken);

        user.Rename(request.FullName, clock.UtcNow);

        auditWriter.Write(new AuditEntry
        {
            Action = AuditActions.UserUpdate,
            EntityType = nameof(User),
            EntityId = user.Id,
            BranchId = user.BranchId,
            CategoryId = user.CategoryId,
        });

        await dbContext.SaveWithConcurrencyCheckAsync(user, cancellationToken);

        return ToDto(user, dbContext.Read(user), null);
    }

    public async Task<UserDto> ActivateAsync(Guid userId, UserActionRequest request, CancellationToken cancellationToken)
    {
        var user = await FindManageableAsync(userId, cancellationToken);
        await tenantState.EnsureWritableAsync(user.BranchId, user.CategoryId, cancellationToken);

        dbContext.Expect(user, request.ConcurrencyToken);
        user.Activate(clock.UtcNow);

        auditWriter.Write(new AuditEntry
        {
            Action = AuditActions.UserActivate,
            EntityType = nameof(User),
            EntityId = user.Id,
            BranchId = user.BranchId,
            CategoryId = user.CategoryId,
        });

        // USER-007: a second active Lead in one category is refused by the partial unique index
        // ux_users_active_lead_per_category, not by a prior lookup — two concurrent activations would
        // both pass a service check.
        await SaveTranslatingConstraintsAsync(cancellationToken, user);

        return ToDto(user, dbContext.Read(user), null);
    }

    public async Task<UserDto> DeactivateAsync(Guid userId, UserActionRequest request, CancellationToken cancellationToken)
    {
        var user = await FindManageableAsync(userId, cancellationToken);
        dbContext.Expect(user, request.ConcurrencyToken);

        var now = clock.UtcNow;

        // Increments TokenVersion, which is what actually invalidates outstanding access tokens.
        user.Deactivate(now);

        await RevokeSessionsAsync(user, RefreshTokenRevocationReason.Deactivated, now, cancellationToken);

        // USER-004: pending invitation and reset links die with the account, or a deactivated person
        // could still set a password and the deactivation would be undone from the outside.
        await InvalidateSecurityTokensAsync(user, now, cancellationToken);

        auditWriter.Write(new AuditEntry
        {
            Action = AuditActions.UserDeactivate,
            EntityType = nameof(User),
            EntityId = user.Id,
            BranchId = user.BranchId,
            CategoryId = user.CategoryId,
        });

        await NotifyContourGapsAsync(user, cancellationToken);

        await dbContext.SaveWithConcurrencyCheckAsync(user, cancellationToken);
        tokenVersionValidator.Invalidate(user.Id);

        return ToDto(user, dbContext.Read(user), null);
    }

    public async Task<UserDto> ChangeRoleAsync(Guid userId, ChangeRoleRequest request, CancellationToken cancellationToken)
    {
        var actor = RequireActor();
        var user = await FindManageableAsync(userId, cancellationToken);

        if (!Enum.TryParse<UserRole>(request.Role, ignoreCase: false, out var targetRole))
        {
            throw new ValidationAppException("role", "Роль должна быть одной из: Admin, Lead, Mentor.");
        }

        if (request.Reason is not { Length: >= 5 and <= 500 })
        {
            throw new ValidationAppException("reason", "Причина обязательна и должна содержать от 5 до 500 символов.");
        }

        AdminScope? adminScope = targetRole is UserRole.Admin
            ? ParseRequiredAdminScope(request.AdminScope)
            : null;

        // USER-031 and USER-033: only an Organization Admin may hand out or alter an administrative
        // contour. A Branch Admin promoting somebody to Admin would be manufacturing a peer.
        if (targetRole is UserRole.Admin && actor is not { Role: UserRole.Admin, AdminScope: AdminScope.Organization })
        {
            throw new ForbiddenException(ErrorCodes.Forbidden, "Назначение администратора доступно только Organization Admin.");
        }

        dbContext.Expect(user, request.ConcurrencyToken);

        var (branchId, categoryId) = ResolveTargetScope(user, targetRole, adminScope, request);
        var previousRole = user.Role;
        var previousScope = user.AdminScope;
        var now = clock.UtcNow;

        user.ChangeRole(targetRole, adminScope, branchId, categoryId, now);

        // AUTH-034: any change of access level invalidates issued tokens at once. Without this the
        // person would keep acting under the old scope for up to the token's lifetime.
        await RevokeSessionsAsync(
            user,
            previousScope != adminScope
                ? RefreshTokenRevocationReason.AdminScopeChanged
                : RefreshTokenRevocationReason.RoleChanged,
            now,
            cancellationToken);

        auditWriter.Write(new AuditEntry
        {
            Action = previousScope != adminScope && targetRole is UserRole.Admin
                ? AuditActions.UserChangeAdminScope
                : AuditActions.UserChangeRole,
            EntityType = nameof(User),
            EntityId = user.Id,
            BranchId = user.BranchId,
            CategoryId = user.CategoryId,
            Metadata = JsonSerializer.SerializeToDocument(new
            {
                previousRole = previousRole.ToString(),
                newRole = targetRole.ToString(),
                previousAdminScope = previousScope?.ToString(),
                newAdminScope = adminScope?.ToString(),
                reason = request.Reason,
            }),
        });

        await SaveTranslatingConstraintsAsync(cancellationToken, user);
        tokenVersionValidator.Invalidate(user.Id);

        return ToDto(user, dbContext.Read(user), null);
    }

    public async Task ResendInvitationAsync(Guid userId, CancellationToken cancellationToken)
    {
        var actor = RequireActor();

        var user = await dbContext.Users.FirstOrDefaultAsync(
                u => u.Id == userId && u.OrganizationId == branchContext.EffectiveOrganizationId,
                cancellationToken)
            ?? throw new NotFoundException();

        EnsureVisible(actor, user);

        // USER-009: a Lead may only resend to their own mentors — never to a peer or an administrator.
        if (actor.Role is UserRole.Lead && (user.Role is not UserRole.Mentor || user.CategoryId != actor.CategoryId))
        {
            throw new NotFoundException();
        }

        if (!user.IsActive)
        {
            throw new ConflictException(
                ErrorCodes.ResourceInUse,
                "Приглашение недоступно для деактивированного пользователя.");
        }

        await IssueInvitationAsync(user, cancellationToken);

        auditWriter.Write(new AuditEntry
        {
            Action = AuditActions.UserUpdate,
            EntityType = nameof(User),
            EntityId = user.Id,
            BranchId = user.BranchId,
            CategoryId = user.CategoryId,
            Metadata = JsonSerializer.SerializeToDocument(new { resentInvitation = true }),
        });

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    // -----------------------------------------------------------------
    // Creation rules (USER-031, USER-032)
    // -----------------------------------------------------------------

    /// <summary>
    /// The «who may create whom» matrix of <c>USER-031</c>.
    /// </summary>
    /// <remarks>
    /// The rejection worth spelling out: a Branch Admin cannot create another Branch Admin. Letting a
    /// branch's administrative contour reproduce itself would take the composition of administrators
    /// out of the organization's control and create an escalation path invisible at organization
    /// level. The attempt is a 403 <b>and</b> an audit entry, because it is exactly the kind of thing
    /// a review needs to see.
    /// </remarks>
    private async Task EnsureMayCreateAsync(
        ICurrentUserContext actor,
        UserRole targetRole,
        AdminScope? adminScope,
        CancellationToken cancellationToken)
    {
        var permitted = actor switch
        {
            { Role: UserRole.Admin, AdminScope: AdminScope.Organization } => true,
            { Role: UserRole.Admin, AdminScope: AdminScope.Branch } => targetRole is UserRole.Lead or UserRole.Mentor,
            { Role: UserRole.Lead } => targetRole is UserRole.Mentor,
            _ => false,
        };

        if (permitted)
        {
            return;
        }

        auditWriter.Write(new AuditEntry
        {
            Action = AuditActions.UserCreate,
            EntityType = nameof(User),

            // The actor's own branch: a rejected attempt is an event where the actor stands. An
            // Organization Admin never reaches this path, so a null here cannot occur.
            BranchId = actor.BranchId,
            Result = AuditResult.Failure,
            FailureReason = ErrorCodes.Forbidden,
            Metadata = JsonSerializer.SerializeToDocument(new
            {
                attemptedRole = targetRole.ToString(),
                attemptedAdminScope = adminScope?.ToString(),
            }),
        });

        // Committed before the 403 propagates. The request is about to be aborted, so there is no
        // later unit of work to ride along with, and a security record that vanishes with the failed
        // request is worthless — this is precisely the attempt USER-031 wants visible in a review.
        await dbContext.SaveChangesAsync(cancellationToken);

        throw new ForbiddenException(
            ErrorCodes.Forbidden,
            "Недостаточно прав для создания пользователя с указанной ролью.");
    }

    /// <summary>
    /// Determines the new user's scope. Always server-side (<c>USER-032</c>).
    /// </summary>
    /// <remarks>
    /// For a Branch Admin and a Lead it comes from their own claims. For an Organization Admin it comes
    /// from <c>X-MTF-Branch-Id</c>, which is mandatory when creating a Branch Admin, Lead or Mentor and
    /// must be absent when creating an Organization Admin — that contour belongs to no branch.
    /// </remarks>
    private async Task<User> BuildNewUserAsync(
        ICurrentUserContext actor,
        CreateUserRequest request,
        UserRole targetRole,
        AdminScope? adminScope,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var organizationId = branchContext.EffectiveOrganizationId;

        if (targetRole is UserRole.Admin && adminScope is AdminScope.Organization)
        {
            if (branchContext.EffectiveBranchId is not null)
            {
                throw new ValidationAppException(
                    "X-MTF-Branch-Id",
                    "Organization Admin не принадлежит филиалу: заголовок выбора филиала недопустим для этой операции.");
            }

            return User.CreateOrganizationAdmin(organizationId, request.FullName, request.Email, now);
        }

        // Answers 400 BRANCH_CONTEXT_REQUIRED for an Organization Admin who has not chosen a branch.
        var branchId = actor.BranchId ?? branchContext.RequireBranchForMutation();

        await tenantState.EnsureWritableAsync(branchId, categoryId: null, cancellationToken);

        if (targetRole is UserRole.Admin)
        {
            return User.CreateBranchAdmin(organizationId, branchId, request.FullName, request.Email, now);
        }

        // A Lead creates only inside their own category and cannot name another one; an administrator
        // picks it, and the choice is verified to belong to the effective branch.
        var categoryId = actor.Role is UserRole.Lead
            ? actor.CategoryId!.Value
            : request.CategoryId ?? throw new ValidationAppException("categoryId", "Категория обязательна для Lead и Mentor.");

        await EnsureCategoryBelongsToBranchAsync(categoryId, branchId, cancellationToken);
        await tenantState.EnsureWritableAsync(branchId, categoryId, cancellationToken);

        return targetRole is UserRole.Lead
            ? User.CreateLead(organizationId, branchId, categoryId, request.FullName, request.Email, now)
            : User.CreateMentor(organizationId, branchId, categoryId, request.FullName, request.Email, now);
    }

    /// <summary>
    /// Verifies the target category lives in the effective branch (<c>TEN-024</c>).
    /// </summary>
    /// <remarks>
    /// Application validation is never the only guard: the composite FK
    /// <c>fk_users_category_scope</c> refuses the same combination in the database. Checking here
    /// turns a foreign-key error at commit into a clear 409 naming the offending relation — and,
    /// per <c>TEN-027</c>, without revealing whose category it actually is.
    /// </remarks>
    private async Task EnsureCategoryBelongsToBranchAsync(Guid categoryId, Guid branchId, CancellationToken cancellationToken)
    {
        var belongs = await dbContext.Categories
            .IgnoreQueryFilters()
            .AnyAsync(
                c => c.Id == categoryId
                     && c.BranchId == branchId
                     && c.OrganizationId == branchContext.EffectiveOrganizationId,
                cancellationToken);

        if (!belongs)
        {
            throw new ConflictException(
                ErrorCodes.CrossScopeReference,
                "Категория не принадлежит выбранному филиалу.",
                new Dictionary<string, object?> { ["relation"] = "categoryId" });
        }
    }

    private static AdminScope? ParseAdminScope(CreateUserRequest request, UserRole targetRole)
    {
        if (targetRole is not UserRole.Admin)
        {
            // TEN-014: adminScope is meaningless outside the Admin role, and silently ignoring it
            // would let a caller believe they had set something.
            return request.AdminScope is null
                ? null
                : throw new ValidationAppException("adminScope", "AdminScope применим только к роли Admin.");
        }

        return ParseRequiredAdminScope(request.AdminScope);
    }

    private static AdminScope ParseRequiredAdminScope(string? value) =>
        Enum.TryParse<AdminScope>(value, ignoreCase: false, out var parsed)
            ? parsed
            : throw new ValidationAppException("adminScope", "Для роли Admin требуется AdminScope: Organization или Branch.");

    /// <summary>Computes the scope a role change lands on (<c>USER-008</c>, <c>USER-033</c>).</summary>
    private static (Guid? BranchId, Guid? CategoryId) ResolveTargetScope(
        User user,
        UserRole targetRole,
        AdminScope? adminScope,
        ChangeRoleRequest request) => (targetRole, adminScope) switch
    {
        // Moving into the organization contour clears the branch: it belongs to none.
        (UserRole.Admin, AdminScope.Organization) => (null, null),

        // Moving into a branch contour requires naming the branch, which may differ from the current
        // one; the category is dropped because no administrator has one.
        (UserRole.Admin, AdminScope.Branch) => (
            request.BranchId ?? user.BranchId
                ?? throw new ValidationAppException("branchId", "Для контура Branch требуется указать филиал."),
            null),

        // Becoming a Lead or Mentor requires a category — an Organization Admin has neither branch
        // nor category, so both must be supplied.
        _ => (
            request.BranchId ?? user.BranchId
                ?? throw new ValidationAppException("branchId", "Для роли Lead или Mentor требуется филиал."),
            request.CategoryId ?? user.CategoryId
                ?? throw new ValidationAppException("categoryId", "Для роли Lead или Mentor требуется категория.")),
    };

    // -----------------------------------------------------------------
    // Sessions and invitations
    // -----------------------------------------------------------------

    private async Task RevokeSessionsAsync(
        User user,
        RefreshTokenRevocationReason reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var tokens = await dbContext.RefreshTokens
            .Where(t => t.UserId == user.Id && t.RevokedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var token in tokens)
        {
            token.Revoke(reason, revokedByIp: null, now);
        }
    }

    private async Task InvalidateSecurityTokensAsync(User user, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var tokens = await dbContext.UserSecurityTokens
            .Where(t => t.UserId == user.Id && t.UsedAt == null && t.InvalidatedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var token in tokens)
        {
            token.Invalidate(now);
        }
    }

    /// <summary>
    /// Issues a set-password link and queues the invitation email.
    /// </summary>
    /// <remarks>
    /// <c>USER-034</c>: the message carries the names of the organization, the branch and — for a Lead
    /// or Mentor — the category, and <b>no UUIDs</b>. Internal identifiers in an email are of no use
    /// to the recipient and of considerable use to anyone who intercepts it. The link itself is not in
    /// the payload either: the renderer builds it from the security token when it sends
    /// (<c>NTF-017</c>).
    /// </remarks>
    private async Task IssueInvitationAsync(User user, CancellationToken cancellationToken)
    {
        await authService.IssueSetPasswordLinkAsync(user, ipAddress: null, cancellationToken);

        var organizationName = await dbContext.Organizations
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(o => o.Id == user.OrganizationId)
            .Select(o => o.Name)
            .FirstAsync(cancellationToken);

        var branchName = user.BranchId is { } branchId
            ? await dbContext.Branches
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(b => b.Id == branchId)
                .Select(b => b.Name)
                .FirstOrDefaultAsync(cancellationToken)
            : null;

        var categoryName = user.CategoryId is { } categoryId
            ? await dbContext.Categories
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(c => c.Id == categoryId)
                .Select(c => c.Name)
                .FirstOrDefaultAsync(cancellationToken)
            : null;

        outboxWriter.EnqueueSystem(
            new OutboxEntry
            {
                RecipientUserId = user.Id,
                EventType = NotificationEventTypes.UserInvitation,
                Channel = NotificationChannel.Email,

                // Keyed by the moment of issue, so a resend produces a new message rather than being
                // swallowed by the deduplication index.
                DeduplicationKey = $"invitation:{user.Id:N}:{clock.UtcNow.UtcDateTime:O}",
                CategoryId = user.CategoryId,
                Payload = JsonSerializer.SerializeToDocument(new
                {
                    fullName = user.FullName,
                    organizationName,
                    branchName,
                    categoryName,
                }),
            },
            user.OrganizationId,
            user.BranchId);
    }

    /// <summary>
    /// Raises the notifications for a contour left without cover (<c>USER-005</c>, <c>USER-036</c>).
    /// </summary>
    /// <remarks>
    /// Neither situation blocks the deactivation. Both are valid but observable states, and refusing
    /// would strand an organization whose only administrator has left (<c>TEN-017</c>). Auto-migration
    /// of authority is deliberately not performed.
    /// </remarks>
    private async Task NotifyContourGapsAsync(User user, CancellationToken cancellationToken)
    {
        var localDate = clock.UtcNow.UtcDateTime.ToString("yyyy-MM-dd");

        if (user.Role is UserRole.Lead && user.CategoryId is { } categoryId)
        {
            var hasAnotherLead = await dbContext.Users
                .IgnoreQueryFilters()
                .AnyAsync(
                    u => u.CategoryId == categoryId && u.Role == UserRole.Lead && u.IsActive && u.Id != user.Id,
                    cancellationToken);

            if (!hasAnotherLead)
            {
                await EnqueueToAdminsAsync(
                    user,
                    NotificationEventTypes.CategoryWithoutLead,
                    $"category-no-lead:{categoryId:N}:{localDate}",
                    cancellationToken);
            }
        }

        if (user is { Role: UserRole.Admin, AdminScope: AdminScope.Branch, BranchId: { } branchId })
        {
            var hasAnotherAdmin = await dbContext.Users
                .IgnoreQueryFilters()
                .AnyAsync(
                    u => u.BranchId == branchId
                         && u.Role == UserRole.Admin
                         && u.AdminScope == AdminScope.Branch
                         && u.IsActive
                         && u.Id != user.Id,
                    cancellationToken);

            if (!hasAnotherAdmin)
            {
                await EnqueueToAdminsAsync(
                    user,
                    NotificationEventTypes.BranchWithoutAdmin,
                    $"branch-no-admin:{branchId:N}:{localDate}",
                    cancellationToken);
            }
        }
    }

    private async Task EnqueueToAdminsAsync(
        User subject,
        string eventType,
        string deduplicationKey,
        CancellationToken cancellationToken)
    {
        // BranchWithoutAdmin is organization-level and therefore addressed to the organization
        // administrators only — the branch has nobody left to tell (TEN-042).
        var organizationLevel = NotificationEventTypes.OrganizationLevelEvents.Contains(eventType);

        var recipients = await dbContext.Users
            .IgnoreQueryFilters()
            .Where(u => u.OrganizationId == subject.OrganizationId && u.Role == UserRole.Admin && u.IsActive)
            .Where(u => organizationLevel
                ? u.AdminScope == AdminScope.Organization
                : u.AdminScope == AdminScope.Organization || u.BranchId == subject.BranchId)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);

        foreach (var recipientId in recipients)
        {
            outboxWriter.EnqueueSystem(
                new OutboxEntry
                {
                    RecipientUserId = recipientId,
                    EventType = eventType,
                    Channel = NotificationChannel.Email,
                    DeduplicationKey = $"{deduplicationKey}:{recipientId:N}",
                    CategoryId = organizationLevel ? null : subject.CategoryId,
                    Payload = JsonSerializer.SerializeToDocument(new { subjectFullName = subject.FullName }),
                },
                subject.OrganizationId,
                organizationLevel ? null : subject.BranchId);
        }
    }

    // -----------------------------------------------------------------
    // Visibility and persistence
    // -----------------------------------------------------------------

    private ICurrentUserContext RequireActor() => currentUser.Current ?? throw new UnauthorizedException();

    /// <summary>Narrows a query to what the actor may see (<c>USER-010</c>).</summary>
    private static IQueryable<User> ApplyVisibility(IQueryable<User> source, ICurrentUserContext actor) => actor switch
    {
        { Role: UserRole.Admin, AdminScope: AdminScope.Organization } => source,

        // A Branch Admin sees their branch. Organization Admins have no branch and stay invisible to
        // them — TEN-003 keeps the composition of the organization out of reach.
        { Role: UserRole.Admin, AdminScope: AdminScope.Branch } => source.Where(u => u.BranchId == actor.BranchId),

        { Role: UserRole.Lead } => source.Where(u => u.CategoryId == actor.CategoryId),
        _ => source.Where(_ => false),
    };

    private static void EnsureVisible(ICurrentUserContext actor, User user)
    {
        var visible = actor switch
        {
            { Role: UserRole.Admin, AdminScope: AdminScope.Organization } => true,
            { Role: UserRole.Admin, AdminScope: AdminScope.Branch } => user.BranchId == actor.BranchId,
            { Role: UserRole.Lead } => user.CategoryId == actor.CategoryId,
            _ => false,
        };

        // 404 rather than 403: a user outside the caller's contour must be indistinguishable from one
        // that does not exist (TEN-006).
        if (!visible)
        {
            throw new NotFoundException();
        }
    }

    private async Task<User> FindManageableAsync(Guid userId, CancellationToken cancellationToken)
    {
        var actor = RequireActor();

        // A Lead creates mentors but administers nobody: role, activity and scope are administrative
        // operations (USER-002).
        if (actor.Role is not UserRole.Admin)
        {
            throw new ForbiddenException(ErrorCodes.Forbidden, "Операция доступна только администратору.");
        }

        var user = await dbContext.Users.FirstOrDefaultAsync(
                u => u.Id == userId && u.OrganizationId == branchContext.EffectiveOrganizationId,
                cancellationToken)
            ?? throw new NotFoundException();

        EnsureVisible(actor, user);

        return user;
    }

    private async Task<Dictionary<Guid, BranchSummaryDto>> LoadBranchSummariesAsync(
        IEnumerable<Guid?> branchIds,
        CancellationToken cancellationToken)
    {
        var ids = branchIds.OfType<Guid>().Distinct().ToArray();

        return await dbContext.Branches
            .AsNoTracking()
            .Where(b => ids.Contains(b.Id))
            .Select(b => new BranchSummaryDto(b.Id, b.Name, b.Code, b.IsHeadOffice))
            .ToDictionaryAsync(b => b.Id, cancellationToken);
    }

    private static BranchSummaryDto? BranchOf(Dictionary<Guid, BranchSummaryDto> branches, User user) =>
        user.BranchId is { } branchId ? branches.GetValueOrDefault(branchId) : null;

    private static IQueryable<User> ApplySort(IQueryable<User> source, UserListQuery query)
    {
        var descending = string.Equals(query.Order, "desc", StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(query.Sort)
            && !SortableColumns.Contains(query.Sort, StringComparer.OrdinalIgnoreCase))
        {
            throw new ValidationAppException(
                "sort",
                $"Сортировка допустима только по полям: {string.Join(", ", SortableColumns)}.");
        }

        return query.Sort?.ToLowerInvariant() switch
        {
            "email" => descending ? source.OrderByDescending(u => u.Email) : source.OrderBy(u => u.Email),
            "createdat" => descending ? source.OrderByDescending(u => u.CreatedAt) : source.OrderBy(u => u.CreatedAt),
            _ => descending ? source.OrderByDescending(u => u.FullName) : source.OrderBy(u => u.FullName),
        };
    }

    /// <summary>
    /// Maps the unique-index violations of the users table onto their catalog codes.
    /// </summary>
    /// <remarks>
    /// Both constraints are the real decision-makers rather than a fallback: two concurrent requests
    /// would both pass a prior lookup, and one still has to lose (<c>USER-003</c>, scenario 17 of
    /// Приложение K).
    /// </remarks>
    private async Task SaveTranslatingConstraintsAsync(CancellationToken cancellationToken, User? tracked = null)
    {
        try
        {
            if (tracked is not null)
            {
                await dbContext.SaveWithConcurrencyCheckAsync(tracked, cancellationToken);
            }
            else
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
                                                  {
                                                      SqlState: PostgresErrorCodes.UniqueViolation,
                                                  } postgres)
        {
            throw postgres.ConstraintName switch
            {
                "ux_users_active_lead_per_category" => new ConflictException(
                    ErrorCodes.ActiveLeadAlreadyExists,
                    "В категории уже есть активный тимлид."),

                "ux_users_normalized_email" => new ConflictException(
                    ErrorCodes.ResourceAlreadyExists,
                    "Пользователь с таким email уже существует."),

                _ => exception,
            };
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
                                                  {
                                                      SqlState: PostgresErrorCodes.ForeignKeyViolation,
                                                      ConstraintName: "fk_users_category_scope",
                                                  })
        {
            // The composite FK caught a combination the application check missed. TEN-027: the answer
            // names the offending relation and nothing about who owns the object.
            throw new ConflictException(
                ErrorCodes.CrossScopeReference,
                "Категория не принадлежит выбранному филиалу.",
                new Dictionary<string, object?> { ["relation"] = "categoryId" });
        }
    }

    private static UserDto ToDto(User user, string concurrencyToken, BranchSummaryDto? branch) => new(
        user.Id,
        user.FullName,
        user.Email,
        user.Role.ToString(),
        user.AdminScope?.ToString(),
        user.OrganizationId,
        user.BranchId,
        user.CategoryId,
        user.IsActive,
        user.PasswordHash is not null,
        user.LastLoginAt,
        user.CreatedAt,
        concurrencyToken,
        branch);

    /// <summary>Carries the shadow <c>xmin</c> out of a no-tracking query alongside its entity.</summary>
    private sealed record UserRow(User User, uint Xmin);
}
