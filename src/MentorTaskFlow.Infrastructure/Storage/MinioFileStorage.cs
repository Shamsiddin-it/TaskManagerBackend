using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Application.Common.Exceptions;
using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Domain.Submissions;
using MentorTaskFlow.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;

namespace MentorTaskFlow.Infrastructure.Storage;

/// <summary>Builds the object key of <c>SUB-009</c>.</summary>
/// <remarks>
/// <para>
/// The scope segments come only from the assignment's own fields on the server. Taking any of them
/// from the form, the query string or a header is forbidden — a caller who could choose the prefix
/// could write into another branch's namespace (<c>TEN-061</c>).
/// </para>
/// <para>
/// <c>OriginalFileName</c> is deliberately absent from the key. Names arrive from clients, and a name
/// in a path is how both traversal and collision happen. The submission's own identifier is unique and
/// carries no user input.
/// </para>
/// <para>
/// The prefix is not an authorisation mechanism (<c>TEN-062</c>): access is decided by the checks of
/// <c>SEC-004</c>. It exists so that a branch's objects can be found under one prefix during an
/// incident and exported without a database round trip per object.
/// </para>
/// </remarks>
public static class SubmissionStorageKey
{
    public static string For(Submission submission) => For(
        submission.OrganizationId,
        submission.BranchId,
        submission.CategoryId,
        submission.AssignmentId,
        submission.Id,
        submission.FileExtension);

    public static string For(
        Guid organizationId,
        Guid branchId,
        Guid categoryId,
        Guid assignmentId,
        Guid submissionId,
        FileExtension extension) =>
        $"submissions/{organizationId}/{branchId}/{categoryId}/{assignmentId}/{submissionId}{Submission.SuffixOf(extension)}";
}

/// <inheritdoc />
/// <remarks>
/// Talks to MinIO through the vendor's own client rather than through the AWS SDK. The TZ names MinIO
/// as the storage, and the difference showed: the AWS endpoint resolver builds presigned URLs over
/// <c>https</c> whatever scheme <c>ServiceURL</c> carries, so every URL had to be rewritten afterwards
/// to be usable against a plain-HTTP MinIO. Here the scheme comes from the client's own configuration
/// and the workaround is gone.
/// </remarks>
public sealed class MinioFileStorage(
    IMinioClient client,
    IOptions<StorageOptions> options,
    ILogger<MinioFileStorage> logger) : IFileStorage
{
    private readonly StorageOptions _options = options.Value;

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken)
    {
        try
        {
            await client.PutObjectAsync(
                new PutObjectArgs()
                    .WithBucket(_options.Bucket)
                    .WithObject(key)
                    .WithStreamData(content)

                    // The inspector spools the upload to a seekable temp file, so the length is known
                    // and the object goes up in one part. -1 is the unknown-length path and is kept
                    // only as a fallback: it buffers, which is what the spooling exists to avoid.
                    .WithObjectSize(content.CanSeek ? content.Length : -1)
                    .WithContentType(contentType),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The object never landed, so no orphan is created and no database row will point at it.
            logger.LogError(exception, "Storage rejected an upload for key {Key}.", key);

            throw new ServiceUnavailableException(
                ErrorCodes.StorageUnavailable,
                "Хранилище файлов временно недоступно. Повторите загрузку позже.");
        }
    }

    public Task<Uri> GetDownloadUrlAsync(
        string key,
        string contentType,
        string downloadFileName,
        CancellationToken cancellationToken) =>
        PresignAsync(
            key,
            contentType,
            $"attachment; filename=\"{SanitiseFileName(downloadFileName)}\"");

    public Task<Uri> GetPreviewUrlAsync(string key, string contentType, CancellationToken cancellationToken)
    {
        // SEC-017: inline only for PDF. Serving a presentation inline hands it to whatever the browser
        // has registered for the type, which defeats the sandboxed preview entirely.
        var disposition = contentType is "application/pdf" ? "inline" : "attachment";

        return PresignAsync(key, contentType, disposition);
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await client.BucketExistsAsync(
                new BucketExistsArgs().WithBucket(_options.Bucket),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Storage health probe failed for bucket {Bucket}.", _options.Bucket);
            return false;
        }
    }

    /// <summary>
    /// Issues the short-lived URL that is the only way a file reaches a person (<c>SEC-013</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The response headers are part of the signature, so a recipient cannot turn an <c>attachment</c>
    /// URL into an <c>inline</c> one by editing the query string. <c>X-Content-Type-Options</c> is set
    /// by the gateway in front of storage (<c>SEC-018</c>); the content type comes from the database
    /// row rather than from the object's stored metadata.
    /// </para>
    /// <para>
    /// The overrides are the S3 <c>response-*</c> query parameters, which MinIO signs along with the
    /// rest of the query string — the same mechanism the AWS SDK exposed as
    /// <c>ResponseHeaderOverrides</c>, spelled as the wire protocol spells it.
    /// </para>
    /// </remarks>
    private async Task<Uri> PresignAsync(string key, string contentType, string contentDisposition)
    {
        try
        {
            var url = await client.PresignedGetObjectAsync(
                new PresignedGetObjectArgs()
                    .WithBucket(_options.Bucket)
                    .WithObject(key)
                    .WithExpiry(_options.PresignedUrlMinutes * 60)
                    .WithHeaders(new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["response-content-type"] = contentType,
                        ["response-content-disposition"] = contentDisposition,

                        // SEC-005: the response must not be cached anywhere on the way back.
                        ["response-cache-control"] = "no-store",
                    }));

            return new Uri(url);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Never log the URL itself: it is a bearer credential for the next ten minutes (SEC-020).
            logger.LogError(exception, "Failed to presign an object in bucket {Bucket}.", _options.Bucket);

            throw new ServiceUnavailableException(
                ErrorCodes.StorageUnavailable,
                "Хранилище файлов временно недоступно. Повторите попытку позже.");
        }
    }

    /// <summary>
    /// Strips everything that could break out of a <c>Content-Disposition</c> header.
    /// </summary>
    /// <remarks>
    /// The name is stored as the person typed it, so quotes, control characters and path separators all
    /// have to go before it is placed inside a quoted header value.
    /// </remarks>
    internal static string SanitiseFileName(string fileName)
    {
        var cleaned = new string([.. fileName
            .Where(c => !char.IsControl(c) && c is not ('"' or '\\' or '/')),
        ]).Trim();

        return cleaned.Length == 0 ? "submission" : cleaned;
    }
}
