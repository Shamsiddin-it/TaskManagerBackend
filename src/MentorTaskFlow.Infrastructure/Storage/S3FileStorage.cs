using Amazon.S3;
using Amazon.S3.Model;
using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Application.Common.Exceptions;
using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Domain.Submissions;
using MentorTaskFlow.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
public sealed class S3FileStorage(
    IAmazonS3 client,
    IOptions<StorageOptions> options,
    ILogger<S3FileStorage> logger) : IFileStorage
{
    private readonly StorageOptions _options = options.Value;

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken)
    {
        try
        {
            await client.PutObjectAsync(
                new PutObjectRequest
                {
                    BucketName = _options.Bucket,
                    Key = key,
                    InputStream = content,
                    ContentType = contentType,

                    // The bucket denies anonymous reads (SEC-013); nothing here may widen that.
                    DisablePayloadSigning = false,
                },
                cancellationToken);
        }
        catch (AmazonS3Exception exception)
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
            $"attachment; filename=\"{SanitiseFileName(downloadFileName)}\"",
            cancellationToken);

    public Task<Uri> GetPreviewUrlAsync(string key, string contentType, CancellationToken cancellationToken)
    {
        // SEC-017: inline only for PDF. Serving a presentation inline hands it to whatever the browser
        // has registered for the type, which defeats the sandboxed preview entirely.
        var disposition = contentType is "application/pdf" ? "inline" : "attachment";

        return PresignAsync(key, contentType, disposition, cancellationToken);
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            await client.GetBucketLocationAsync(_options.Bucket, cancellationToken);
            return true;
        }
        catch (AmazonS3Exception exception)
        {
            logger.LogWarning(exception, "Storage health probe failed for bucket {Bucket}.", _options.Bucket);
            return false;
        }
    }

    /// <summary>
    /// Issues the short-lived URL that is the only way a file reaches a person (<c>SEC-013</c>).
    /// </summary>
    /// <remarks>
    /// The response headers are part of the signature, so a recipient cannot turn an <c>attachment</c>
    /// URL into an <c>inline</c> one by editing the query string. <c>X-Content-Type-Options</c> is set
    /// by the gateway in front of storage (<c>SEC-018</c>); the content type comes from the database
    /// row rather than from the object's stored metadata.
    /// </remarks>
    private async Task<Uri> PresignAsync(
        string key,
        string contentType,
        string contentDisposition,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = await client.GetPreSignedURLAsync(new GetPreSignedUrlRequest
            {
                BucketName = _options.Bucket,
                Key = key,
                Verb = HttpVerb.GET,
                Expires = DateTime.UtcNow.AddMinutes(_options.PresignedUrlMinutes),
                ResponseHeaderOverrides = new ResponseHeaderOverrides
                {
                    ContentType = contentType,
                    ContentDisposition = contentDisposition,
                    CacheControl = "no-store",
                },
            });

            return Normalise(url);
        }
        catch (AmazonS3Exception exception)
        {
            // Never log the URL itself: it is a bearer credential for the next ten minutes (SEC-020).
            logger.LogError(exception, "Failed to presign an object in bucket {Bucket}.", _options.Bucket);

            throw new ServiceUnavailableException(
                ErrorCodes.StorageUnavailable,
                "Хранилище файлов временно недоступно. Повторите попытку позже.");
        }
    }

    /// <summary>
    /// Forces the URL onto the scheme the endpoint is actually configured with.
    /// </summary>
    /// <remarks>
    /// The AWS SDK's endpoint resolver builds presigned URLs over <c>https</c> regardless of the scheme
    /// of <c>ServiceURL</c>, which points at a plain-HTTP MinIO in development and in the test
    /// environment; a client following such a URL fails with a TLS framing error rather than anything
    /// diagnosable. Rewriting the scheme is safe: SigV4 signs the host, the path, the query and the
    /// signed headers — the scheme is not among them, so the signature stays valid.
    /// </remarks>
    private Uri Normalise(string url)
    {
        var builder = new UriBuilder(url)
        {
            Scheme = _options.UseSsl ? Uri.UriSchemeHttps : Uri.UriSchemeHttp,
        };

        // UriBuilder substitutes the default port for the new scheme; the endpoint's own port must win.
        builder.Port = new Uri(url).Port;

        return builder.Uri;
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
            .Where(c => !char.IsControl(c) && c is not ('"' or '\\' or '/' or '\r' or '\n')),
        ]).Trim();

        return cleaned.Length == 0 ? "submission" : cleaned;
    }
}
