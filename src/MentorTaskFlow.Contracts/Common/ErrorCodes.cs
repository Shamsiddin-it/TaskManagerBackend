namespace MentorTaskFlow.Contracts.Common;

/// <summary>
/// Stable machine-readable error codes. Normative source: TZ v2.2, Приложение C (54 codes).
/// <para>
/// A code never changes meaning between versions (<c>API-021</c>). Titles and details may change,
/// codes may not. Codes are consumed by the frontend and by tests.
/// </para>
/// <para>
/// Codes that confirm the existence of an object outside the caller's scope — <c>FOREIGN_BRANCH</c>,
/// <c>FOREIGN_ORGANIZATION</c>, <c>BRANCH_NOT_FOUND</c>, <c>ORGANIZATION_NOT_FOUND</c> — are absent
/// deliberately and must never be added (<c>TEN-006</c>, <c>TEN-007</c>). All those situations return
/// the single <see cref="ResourceNotFound"/>.
/// </para>
/// </summary>
public static class ErrorCodes
{
    // ---- 400 Bad Request -------------------------------------------------
    /// <summary>DTO validation, unknown member in body, out-of-range value, missing concurrencyToken.</summary>
    public const string ValidationFailed = "VALIDATION_FAILED";

    /// <summary>Set/reset password token not found, expired, used or invalidated.</summary>
    public const string SecurityTokenInvalid = "SECURITY_TOKEN_INVALID";

    /// <summary>Organization Admin performs a branch-scoped mutation without <c>X-MTF-Branch-Id</c>.</summary>
    public const string BranchContextRequired = "BRANCH_CONTEXT_REQUIRED";

    // ---- 401 Unauthorized ------------------------------------------------
    public const string Unauthorized = "UNAUTHORIZED";
    public const string InvalidCredentials = "INVALID_CREDENTIALS";
    public const string TokenExpired = "TOKEN_EXPIRED";
    public const string TokenVersionMismatch = "TOKEN_VERSION_MISMATCH";
    public const string RefreshTokenInvalid = "REFRESH_TOKEN_INVALID";
    public const string RefreshTokenReuseDetected = "REFRESH_TOKEN_REUSE_DETECTED";
    public const string UserDeactivated = "USER_DEACTIVATED";

    // ---- 403 Forbidden ---------------------------------------------------
    public const string Forbidden = "FORBIDDEN";
    public const string CsrfValidationFailed = "CSRF_VALIDATION_FAILED";
    public const string OrganizationInactive = "ORGANIZATION_INACTIVE";
    public const string BranchInactive = "BRANCH_INACTIVE";

    /// <summary>Branch Admin, Lead or Mentor sent <c>X-MTF-Branch-Id</c> — even with their own id (<c>TEN-032</c>).</summary>
    public const string ScopeOverrideForbidden = "SCOPE_OVERRIDE_FORBIDDEN";

    public const string CategoryInactive = "CATEGORY_INACTIVE";
    public const string SelfReviewForbidden = "SELF_REVIEW_FORBIDDEN";
    public const string InsufficientSampleSize = "INSUFFICIENT_SAMPLE_SIZE";

    // ---- 404 Not Found ---------------------------------------------------
    /// <summary>
    /// Object does not exist, or belongs to another Organization / Branch / Category / Mentor.
    /// All five cases produce a byte-identical response (<c>TEN-006</c>).
    /// </summary>
    public const string ResourceNotFound = "RESOURCE_NOT_FOUND";

    // ---- 409 Conflict ----------------------------------------------------
    public const string ConcurrencyConflict = "CONCURRENCY_CONFLICT";
    public const string CrossScopeReference = "CROSS_SCOPE_REFERENCE";
    public const string BranchAlreadyExists = "BRANCH_ALREADY_EXISTS";
    public const string BranchHasActiveUsers = "BRANCH_HAS_ACTIVE_USERS";
    public const string HeadOfficeRequired = "HEAD_OFFICE_REQUIRED";
    public const string HeadOfficeDeactivationForbidden = "HEAD_OFFICE_DEACTIVATION_FORBIDDEN";
    public const string BranchChangeBlocked = "BRANCH_CHANGE_BLOCKED";
    public const string AssignmentInvalidStatusTransition = "ASSIGNMENT_INVALID_STATUS_TRANSITION";
    public const string AssignmentTerminal = "ASSIGNMENT_TERMINAL";
    public const string ReassignNotAllowed = "REASSIGN_NOT_ALLOWED";
    public const string AssigneeInactive = "ASSIGNEE_INACTIVE";
    public const string SubmissionNotAllowed = "SUBMISSION_NOT_ALLOWED";
    public const string LateSubmissionDisabled = "LATE_SUBMISSION_DISABLED";
    public const string SubmissionDuplicateContent = "SUBMISSION_DUPLICATE_CONTENT";
    public const string ReviewAlreadyExists = "REVIEW_ALREADY_EXISTS";
    public const string ReviewNotLatestSubmission = "REVIEW_NOT_LATEST_SUBMISSION";
    public const string ActiveLeadAlreadyExists = "ACTIVE_LEAD_ALREADY_EXISTS";
    public const string CategoryChangeBlocked = "CATEGORY_CHANGE_BLOCKED";
    public const string CategoryHasActiveUsers = "CATEGORY_HAS_ACTIVE_USERS";
    public const string ResourceAlreadyExists = "RESOURCE_ALREADY_EXISTS";
    public const string ResourceInUse = "RESOURCE_IN_USE";
    public const string TelegramAlreadyBound = "TELEGRAM_ALREADY_BOUND";
    public const string TelegramBindTokenInvalid = "TELEGRAM_BIND_TOKEN_INVALID";

    // ---- 413 Payload Too Large ------------------------------------------
    public const string PayloadTooLarge = "PAYLOAD_TOO_LARGE";
    public const string FileTooLarge = "FILE_TOO_LARGE";

    // ---- 415 Unsupported Media Type -------------------------------------
    public const string FileTypeNotAllowed = "FILE_TYPE_NOT_ALLOWED";

    // ---- 422 Unprocessable Content --------------------------------------
    public const string FileEmpty = "FILE_EMPTY";
    public const string FileSignatureMismatch = "FILE_SIGNATURE_MISMATCH";
    public const string PptxStructureInvalid = "PPTX_STRUCTURE_INVALID";
    public const string ZipSafetyLimitExceeded = "ZIP_SAFETY_LIMIT_EXCEEDED";

    // ---- 429 Too Many Requests ------------------------------------------
    public const string RateLimitExceeded = "RATE_LIMIT_EXCEEDED";
    public const string AiRegenerationLimit = "AI_REGENERATION_LIMIT";

    // ---- 500 Internal Server Error --------------------------------------
    public const string InternalError = "INTERNAL_ERROR";

    // ---- 503 Service Unavailable ----------------------------------------
    public const string AiProviderUnavailable = "AI_PROVIDER_UNAVAILABLE";
    public const string StorageUnavailable = "STORAGE_UNAVAILABLE";

    /// <summary>
    /// The canonical HTTP status for every code. Приложение C is the single source of truth;
    /// the catalog must contain exactly 54 entries (verified by <c>ErrorCatalogTests</c>).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, int> StatusByCode = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        [ValidationFailed] = 400,
        [SecurityTokenInvalid] = 400,
        [BranchContextRequired] = 400,

        [Unauthorized] = 401,
        [InvalidCredentials] = 401,
        [TokenExpired] = 401,
        [TokenVersionMismatch] = 401,
        [RefreshTokenInvalid] = 401,
        [RefreshTokenReuseDetected] = 401,
        [UserDeactivated] = 401,

        [Forbidden] = 403,
        [CsrfValidationFailed] = 403,
        [OrganizationInactive] = 403,
        [BranchInactive] = 403,
        [ScopeOverrideForbidden] = 403,
        [CategoryInactive] = 403,
        [SelfReviewForbidden] = 403,
        [InsufficientSampleSize] = 403,

        [ResourceNotFound] = 404,

        [ConcurrencyConflict] = 409,
        [CrossScopeReference] = 409,
        [BranchAlreadyExists] = 409,
        [BranchHasActiveUsers] = 409,
        [HeadOfficeRequired] = 409,
        [HeadOfficeDeactivationForbidden] = 409,
        [BranchChangeBlocked] = 409,
        [AssignmentInvalidStatusTransition] = 409,
        [AssignmentTerminal] = 409,
        [ReassignNotAllowed] = 409,
        [AssigneeInactive] = 409,
        [SubmissionNotAllowed] = 409,
        [LateSubmissionDisabled] = 409,
        [SubmissionDuplicateContent] = 409,
        [ReviewAlreadyExists] = 409,
        [ReviewNotLatestSubmission] = 409,
        [ActiveLeadAlreadyExists] = 409,
        [CategoryChangeBlocked] = 409,
        [CategoryHasActiveUsers] = 409,
        [ResourceAlreadyExists] = 409,
        [ResourceInUse] = 409,
        [TelegramAlreadyBound] = 409,
        [TelegramBindTokenInvalid] = 409,

        [PayloadTooLarge] = 413,
        [FileTooLarge] = 413,

        [FileTypeNotAllowed] = 415,

        [FileEmpty] = 422,
        [FileSignatureMismatch] = 422,
        [PptxStructureInvalid] = 422,
        [ZipSafetyLimitExceeded] = 422,

        [RateLimitExceeded] = 429,
        [AiRegenerationLimit] = 429,

        [InternalError] = 500,

        [AiProviderUnavailable] = 503,
        [StorageUnavailable] = 503,
    };

    /// <summary>
    /// Codes that must never exist: each would confirm the existence of an object beyond the
    /// caller's visibility and turn an error message into a reconnaissance channel (<c>TEN-006</c>).
    /// Enforced by <c>ErrorCatalogTests.Forbidden_codes_are_absent</c>.
    /// </summary>
    public static readonly IReadOnlySet<string> PermanentlyForbiddenCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        "FOREIGN_BRANCH",
        "FOREIGN_ORGANIZATION",
        "BRANCH_NOT_FOUND",
        "ORGANIZATION_NOT_FOUND",
        "ASSIGNMENT_ALREADY_EXISTS",
    };

    /// <summary>Builds the RFC 9457 <c>type</c> URI for a code: <c>LATE_SUBMISSION_DISABLED</c> → <c>.../late-submission-disabled</c>.</summary>
    public static string ToTypeUri(string code) =>
        $"https://mentortaskflow/errors/{code.Replace('_', '-').ToLowerInvariant()}";
}
