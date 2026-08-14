namespace MentorTaskFlow.Contracts.Submissions;

/// <summary>
/// One version of a mentor's work (Приложение D.5).
/// </summary>
/// <remarks>
/// There is no <c>storageKey</c> and there never will be. The key is a server-side detail; a file is
/// reached through a presigned URL issued after the permission checks are repeated in full
/// (<c>SUB-008</c>, <c>SEC-004</c>). <c>hasPreview</c> replaces exposing the preview key: the interface
/// needs to know whether to offer a viewer, which is a boolean, not a location.
/// </remarks>
public sealed record SubmissionDto(
    Guid Id,
    Guid AssignmentId,
    int VersionNumber,
    string OriginalFileName,
    string ContentType,
    string FileExtension,
    long FileSizeBytes,
    string Sha256Hash,
    bool IsLate,
    Guid SubmittedById,
    DateTimeOffset SubmittedAt,
    bool HasPreview);

/// <summary>
/// A presigned URL and the moment it stops working.
/// </summary>
/// <remarks>
/// <c>expiresAt</c> is returned so a client can refresh before the link dies rather than discovering
/// it through a 403 from storage. The URL itself is a bearer credential for its lifetime and is kept
/// out of every log (<c>SEC-020</c>).
/// </remarks>
public sealed record FileUrlDto(string Url, DateTimeOffset ExpiresAt);
