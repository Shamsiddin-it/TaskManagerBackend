namespace MentorTaskFlow.Application.Common.Abstractions;

/// <summary>
/// Object storage for submitted files (TZ 17.1).
/// </summary>
/// <remarks>
/// The database keeps only the key; the bytes live in S3-compatible storage (<c>SUB-007</c>). The
/// interface deliberately offers no «read the object» method: a file reaches a person through a
/// short-lived presigned URL and never through the API process, which keeps 50 MB downloads off the
/// application's memory and its logs (<c>SUB-008</c>).
/// </remarks>
public interface IFileStorage
{
    /// <summary>
    /// Uploads an object under a server-generated key.
    /// </summary>
    /// <remarks>
    /// Called only after every validation of 17.2 has passed, so a rejected file never reaches the
    /// bucket. If the database transaction that follows fails, the object is left behind as an orphan
    /// and removed by the daily cleanup — the reverse order would produce a row pointing at nothing
    /// (<c>SUB-030</c>, <c>SUB-032</c>).
    /// </remarks>
    Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken);

    /// <summary>
    /// Issues a presigned URL for downloading, forcing a save dialog.
    /// </summary>
    /// <param name="downloadFileName">
    /// Shown to the person instead of the opaque key. Sanitised by the implementation, because it ends
    /// up inside a <c>Content-Disposition</c> header (<c>SEC-017</c>).
    /// </param>
    Task<Uri> GetDownloadUrlAsync(string key, string contentType, string downloadFileName, CancellationToken cancellationToken);

    /// <summary>
    /// Issues a presigned URL for viewing in place.
    /// </summary>
    /// <remarks>
    /// <c>inline</c> is used for <c>application/pdf</c> only. A PPTX served inline would be handed to
    /// whatever handler the browser has for it, which is the opposite of what a sandboxed preview is
    /// for (<c>SEC-017</c>, 17.5).
    /// </remarks>
    Task<Uri> GetPreviewUrlAsync(string key, string contentType, CancellationToken cancellationToken);

    /// <summary>Whether the bucket is reachable — the storage half of the readiness probe (<c>REL-008</c>).</summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken);
}
