using MentorTaskFlow.Domain.Assignments;
using MentorTaskFlow.Domain.Common;

namespace MentorTaskFlow.Domain.Submissions;

/// <summary>The two accepted file types (<c>SUB-011</c>).</summary>
public enum FileExtension
{
    Pdf = 0,
    Pptx = 1,
}

/// <summary>
/// One uploaded version of a mentor's work (TZ 10.7).
/// </summary>
/// <remarks>
/// <para>
/// Immutable: no role edits or deletes a submission, and a re-upload always creates a new row with the
/// next version number (<c>SUB-020</c>). Hence no concurrency token — the entity is never updated
/// (11.6).
/// </para>
/// <para>
/// The scope fields are copied from the assignment rather than from the request. They are what the
/// storage key is built from, and accepting them from a client would let a caller write into another
/// branch's prefix (<c>TEN-061</c>).
/// </para>
/// </remarks>
public sealed class Submission : BaseEntity
{
    public const int OriginalFileNameMaxLength = 255;
    public const int StorageKeyMaxLength = 320;
    public const int Sha256Length = 64;

    public Guid AssignmentId { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid BranchId { get; private set; }

    public Guid CategoryId { get; private set; }

    /// <summary>Starts at 1; assigned under the assignment's row lock (12.5).</summary>
    public int VersionNumber { get; private set; }

    /// <summary>Never leaves the server (<c>SUB-008</c>).</summary>
    public string StorageKey { get; private set; } = null!;

    public string OriginalFileName { get; private set; } = null!;

    public string ContentType { get; private set; } = null!;

    public FileExtension FileExtension { get; private set; }

    public long FileSizeBytes { get; private set; }

    /// <summary>Lower-case hex of the whole file. The index is <b>not</b> unique (<c>SUB-027</c>).</summary>
    public string Sha256Hash { get; private set; } = null!;

    public bool IsLate { get; private set; }

    public Guid SubmittedById { get; private set; }

    public DateTimeOffset SubmittedAt { get; private set; }

    /// <summary>Equal to <see cref="StorageKey"/> for PDF, null for PPTX in Release 1.0 (17.5).</summary>
    public string? PreviewStorageKey { get; private set; }

    /// <summary>Reserved for the asynchronous PPTX conversion of a later version; always null here.</summary>
    public string? ConversionStatus { get; private set; }

    private Submission()
    {
    }

    /// <summary>
    /// Records a version of the work.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The identifier is supplied rather than generated here: the storage key is built from it and the
    /// object is uploaded before the row exists, so both must name the same submission
    /// (<c>SUB-009</c>, <c>SUB-030</c>).
    /// </para>
    /// <para>
    /// <paramref name="isLate"/> is decided by the caller because it depends on the assignment's state
    /// and on the clock, both of which live outside this entity (<c>SUB-025</c>).
    /// </para>
    /// </remarks>
    public static Submission Record(
        Guid id,
        Assignment assignment,
        int versionNumber,
        string storageKey,
        string originalFileName,
        FileExtension extension,
        long fileSizeBytes,
        string sha256Hash,
        bool isLate,
        Guid submittedById,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(assignment);

        if (versionNumber < 1)
        {
            throw new DomainException(DomainErrorCodes.ValidationFailed, "VersionNumber начинается с 1.");
        }

        // The uploader is the executor by definition. A mismatch means the ownership check of 17.2
        // step 3 was skipped, and the row would attribute one person's work to another.
        if (submittedById != assignment.AssignedToId)
        {
            throw new DomainException(
                DomainErrorCodes.ValidationFailed,
                "Загрузить работу может только исполнитель задачи.");
        }

        if (fileSizeBytes <= 0)
        {
            throw new DomainException(DomainErrorCodes.ValidationFailed, "Размер файла должен быть больше нуля.");
        }

        var trimmedName = (originalFileName ?? string.Empty).Trim();

        if (trimmedName.Length is 0 or > OriginalFileNameMaxLength)
        {
            throw new DomainException(
                DomainErrorCodes.ValidationFailed,
                $"Имя файла обязательно и не длиннее {OriginalFileNameMaxLength} символов.");
        }

        if (sha256Hash is not { Length: Sha256Length })
        {
            throw new DomainException(DomainErrorCodes.ValidationFailed, "Sha256Hash обязан быть 64-символьным hex.");
        }

        var contentType = ContentTypeOf(extension);

        return new Submission
        {
            Id = id,
            AssignmentId = assignment.Id,
            OrganizationId = assignment.OrganizationId,
            BranchId = assignment.BranchId,
            CategoryId = assignment.CategoryId,
            VersionNumber = versionNumber,
            StorageKey = storageKey,
            OriginalFileName = trimmedName,
            ContentType = contentType,
            FileExtension = extension,
            FileSizeBytes = fileSizeBytes,
            Sha256Hash = sha256Hash.ToLowerInvariant(),
            IsLate = isLate,
            SubmittedById = submittedById,
            SubmittedAt = now,

            // PDF is previewed as itself; PPTX has no preview in Release 1.0, and null is what says so.
            PreviewStorageKey = extension is FileExtension.Pdf ? storageKey : null,
            ConversionStatus = null,
            CreatedAt = now,
        };
    }

    public static string ContentTypeOf(FileExtension extension) => extension switch
    {
        FileExtension.Pdf => "application/pdf",
        FileExtension.Pptx => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        _ => throw new DomainException(DomainErrorCodes.ValidationFailed, "Неизвестное расширение файла."),
    };

    public static string SuffixOf(FileExtension extension) => extension switch
    {
        FileExtension.Pdf => ".pdf",
        FileExtension.Pptx => ".pptx",
        _ => throw new DomainException(DomainErrorCodes.ValidationFailed, "Неизвестное расширение файла."),
    };
}
