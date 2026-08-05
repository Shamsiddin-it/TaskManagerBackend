using MentorTaskFlow.Domain.Common;
using MentorTaskFlow.Domain.Tenancy;

namespace MentorTaskFlow.Domain.Users;

/// <summary>
/// A system account (TZ 10.1).
/// </summary>
/// <remarks>
/// <para>
/// The scope fields admit <b>exactly four</b> combinations (<c>USER-023</c>). Anything else is
/// rejected by the CHECK constraints <c>ck_users_scope_shape</c>, <c>ck_users_role_admin_scope</c>
/// and <c>ck_users_role_category</c>; the factories below are the application-side half of that
/// guarantee, never its only half (<c>TEN-023</c>).
/// </para>
/// <list type="table">
///   <item><description>Admin + Organization → OrganizationId, BranchId NULL, CategoryId NULL</description></item>
///   <item><description>Admin + Branch → OrganizationId, BranchId, CategoryId NULL</description></item>
///   <item><description>Lead → OrganizationId, BranchId, CategoryId, AdminScope NULL</description></item>
///   <item><description>Mentor → OrganizationId, BranchId, CategoryId, AdminScope NULL</description></item>
/// </list>
/// </remarks>
public sealed class User : AuditableEntity
{
    public const int FullNameMinLength = 2;
    public const int FullNameMaxLength = 100;
    public const int EmailMaxLength = 254;

    public string FullName { get; private set; } = null!;

    /// <summary>Stored as the user typed it; the uniqueness index uses <see cref="NormalizedEmail"/>.</summary>
    public string Email { get; private set; } = null!;

    /// <summary>
    /// <b>Globally</b> unique, not unique per organization (<c>TEN-028</c>). Login is Email + password
    /// with no tenant selector, so per-organization uniqueness would force either an organization
    /// field on the sign-in form or an ambiguous account resolution. The consequence — one address
    /// cannot be reused across two organizations — is accepted deliberately.
    /// </summary>
    public string NormalizedEmail { get; private set; } = null!;

    /// <summary>
    /// Null until the invitation flow completes. A user in that state cannot sign in: login returns
    /// 401 <c>INVALID_CREDENTIALS</c> without revealing why (<c>USER-021</c>). Generated passwords are
    /// never produced, mailed or displayed (<c>AUTH-019</c>).
    /// </summary>
    public string? PasswordHash { get; private set; }

    public UserRole Role { get; private set; }

    public AdminScope? AdminScope { get; private set; }

    /// <summary>
    /// Required for every role without exception. Immutable: moving a user between organizations is
    /// unsupported and has no endpoint (<c>ORG-023</c>).
    /// </summary>
    public Guid OrganizationId { get; private set; }

    /// <summary>Null only for an Organization Admin. Changed only through <c>change-branch</c> (TZ 39.6).</summary>
    public Guid? BranchId { get; private set; }

    /// <summary>
    /// Required for Lead and Mentor, null for both Admins. When set, the composite FK
    /// <c>fk_users_category_scope</c> guarantees the category belongs to the very same organization
    /// and branch — «a Lead of branch A attached to a category of branch B» is physically impossible
    /// (<c>USER-024</c>).
    /// </summary>
    public Guid? CategoryId { get; private set; }

    public string? TelegramChatId { get; private set; }

    /// <summary>
    /// Incremented on any change to scope or access level: password, role, admin scope, category,
    /// branch, deactivation, refresh-token reuse (<c>AUTH-034</c>). Carried in the access token as
    /// the <c>tv</c> claim, which is what makes revocation effective within 30 seconds.
    /// </summary>
    public int TokenVersion { get; private set; }

    public bool IsActive { get; private set; }

    public int FailedLoginCount { get; private set; }

    public DateTimeOffset? LockoutUntil { get; private set; }

    public DateTimeOffset? LastLoginAt { get; private set; }

    private User()
    {
    }

    /// <summary>Organization Admin: the whole organization, bound to no branch and no category.</summary>
    public static User CreateOrganizationAdmin(Guid organizationId, string fullName, string email, DateTimeOffset now) =>
        Create(organizationId, null, null, UserRole.Admin, Tenancy.AdminScope.Organization, fullName, email, now);

    /// <summary>Branch Admin: one branch. Created only by an Organization Admin (<c>USER-031</c>).</summary>
    public static User CreateBranchAdmin(Guid organizationId, Guid branchId, string fullName, string email, DateTimeOffset now) =>
        Create(organizationId, branchId, null, UserRole.Admin, Tenancy.AdminScope.Branch, fullName, email, now);

    /// <summary>
    /// Lead of a category. At most one active Lead may exist per category — enforced by the partial
    /// unique index <c>ux_users_active_lead_per_category</c>, not by a service check (<c>USER-003</c>).
    /// </summary>
    public static User CreateLead(Guid organizationId, Guid branchId, Guid categoryId, string fullName, string email, DateTimeOffset now) =>
        Create(organizationId, branchId, categoryId, UserRole.Lead, null, fullName, email, now);

    public static User CreateMentor(Guid organizationId, Guid branchId, Guid categoryId, string fullName, string email, DateTimeOffset now) =>
        Create(organizationId, branchId, categoryId, UserRole.Mentor, null, fullName, email, now);

    private static User Create(
        Guid organizationId,
        Guid? branchId,
        Guid? categoryId,
        UserRole role,
        AdminScope? adminScope,
        string fullName,
        string email,
        DateTimeOffset now)
    {
        var trimmedName = (fullName ?? string.Empty).Trim();
        var trimmedEmail = (email ?? string.Empty).Trim();

        if (organizationId == Guid.Empty)
        {
            throw new DomainException(DomainErrorCodes.ValidationFailed, "OrganizationId обязателен для всех ролей.");
        }

        if (trimmedName.Length is < FullNameMinLength or > FullNameMaxLength)
        {
            throw new DomainException(
                DomainErrorCodes.ValidationFailed,
                $"ФИО должно содержать от {FullNameMinLength} до {FullNameMaxLength} символов.");
        }

        if (trimmedEmail.Length is 0 or > EmailMaxLength || !trimmedEmail.Contains('@'))
        {
            throw new DomainException(DomainErrorCodes.ValidationFailed, "Email обязателен и должен быть корректным.");
        }

        EnsureScopeShape(role, adminScope, branchId, categoryId);

        return new User
        {
            OrganizationId = organizationId,
            BranchId = branchId,
            CategoryId = categoryId,
            Role = role,
            AdminScope = adminScope,
            FullName = trimmedName,
            Email = trimmedEmail,
            NormalizedEmail = Normalization.ToNormalized(trimmedEmail),
            PasswordHash = null,
            TokenVersion = 0,
            IsActive = true,
            FailedLoginCount = 0,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// The application-side mirror of <c>ck_users_scope_shape</c>. Kept as one method so the rule
    /// exists in exactly one place: a second copy would inevitably drift from the CHECK constraint.
    /// </summary>
    public static void EnsureScopeShape(UserRole role, AdminScope? adminScope, Guid? branchId, Guid? categoryId)
    {
        var valid = (role, adminScope) switch
        {
            (UserRole.Admin, Tenancy.AdminScope.Organization) => branchId is null && categoryId is null,
            (UserRole.Admin, Tenancy.AdminScope.Branch) => branchId is not null && categoryId is null,
            (UserRole.Admin, null) => false,
            (UserRole.Lead or UserRole.Mentor, null) => branchId is not null && categoryId is not null,
            (UserRole.Lead or UserRole.Mentor, not null) => false,
            _ => false,
        };

        if (!valid)
        {
            throw new DomainException(
                DomainErrorCodes.ValidationFailed,
                "Недопустимое сочетание Role/AdminScope/BranchId/CategoryId (USER-023).");
        }
    }

    /// <summary>Sets the first password through the invitation flow, or replaces it on reset.</summary>
    public void SetPasswordHash(string passwordHash, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(passwordHash))
        {
            throw new DomainException(DomainErrorCodes.ValidationFailed, "PasswordHash не может быть пустым.");
        }

        PasswordHash = passwordHash;
        TokenVersion++;
        FailedLoginCount = 0;
        LockoutUntil = null;
        UpdatedAt = now;
    }

    /// <summary>
    /// Moves the user to another category <b>within the same branch</b> (<c>USER-037</c>). A transfer
    /// that also changes branch is a different operation entirely (TZ 39.6).
    /// </summary>
    public void ChangeCategory(Guid newCategoryId, DateTimeOffset now)
    {
        if (Role is UserRole.Admin)
        {
            throw new DomainException(
                DomainErrorCodes.ValidationFailed,
                "У администратора нет категории; смена категории неприменима.");
        }

        if (newCategoryId == Guid.Empty)
        {
            throw new DomainException(DomainErrorCodes.ValidationFailed, "NewCategoryId обязателен.");
        }

        CategoryId = newCategoryId;
        TokenVersion++;
        UpdatedAt = now;
    }

    /// <summary>
    /// Moves the user to another branch. Historical rows are never re-scoped: assignments,
    /// submissions, reviews and events keep their original branch, so neither branch's analytics is
    /// distorted retroactively (<c>TEN-018</c>).
    /// </summary>
    public void ChangeBranch(Guid newBranchId, Guid? newCategoryId, DateTimeOffset now)
    {
        if (newBranchId == Guid.Empty)
        {
            throw new DomainException(DomainErrorCodes.ValidationFailed, "NewBranchId обязателен.");
        }

        EnsureScopeShape(Role, AdminScope, newBranchId, newCategoryId);

        BranchId = newBranchId;
        CategoryId = newCategoryId;
        TokenVersion++;
        UpdatedAt = now;
    }

    /// <summary>Changes role and, for an Admin, the administrative contour (<c>USER-008</c>, <c>USER-033</c>).</summary>
    public void ChangeRole(UserRole newRole, AdminScope? newAdminScope, Guid? newBranchId, Guid? newCategoryId, DateTimeOffset now)
    {
        EnsureScopeShape(newRole, newAdminScope, newBranchId, newCategoryId);

        Role = newRole;
        AdminScope = newAdminScope;
        BranchId = newBranchId;
        CategoryId = newCategoryId;
        TokenVersion++;
        UpdatedAt = now;
    }

    /// <summary>
    /// Deactivation blocks sign-in. The caller must, in the same transaction, revoke every refresh
    /// token and invalidate active security tokens (<c>USER-004</c>).
    /// </summary>
    public void Deactivate(DateTimeOffset now)
    {
        IsActive = false;
        TokenVersion++;
        UpdatedAt = now;
    }

    public void Activate(DateTimeOffset now)
    {
        IsActive = true;
        UpdatedAt = now;
    }

    public void BindTelegram(string telegramChatId, DateTimeOffset now)
    {
        TelegramChatId = telegramChatId;
        UpdatedAt = now;
    }

    public void UnbindTelegram(DateTimeOffset now)
    {
        TelegramChatId = null;
        UpdatedAt = now;
    }

    public void RegisterSuccessfulLogin(DateTimeOffset now)
    {
        FailedLoginCount = 0;
        LockoutUntil = null;
        LastLoginAt = now;
        UpdatedAt = now;
    }

    public void RegisterFailedLogin(int lockoutThreshold, TimeSpan lockoutDuration, DateTimeOffset now)
    {
        FailedLoginCount++;

        if (FailedLoginCount >= lockoutThreshold)
        {
            LockoutUntil = now.Add(lockoutDuration);
        }

        UpdatedAt = now;
    }

    /// <summary>Invalidates every issued token without any other state change (<c>AUTH-027</c>).</summary>
    public void BumpTokenVersion(DateTimeOffset now)
    {
        TokenVersion++;
        UpdatedAt = now;
    }
}
