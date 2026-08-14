using MentorTaskFlow.Api.Authorization;
using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Application.Common.Exceptions;
using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Contracts.Submissions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MentorTaskFlow.Api.Controllers;

/// <summary>
/// Uploading and reading submitted work (Приложение D.5, TZ 15.6, 17).
/// </summary>
/// <remarks>
/// There is no <c>DELETE</c> and no <c>PUT</c>: a submission is immutable, and a re-upload creates a
/// new version rather than replacing one (<c>SUB-020</c>). Objects leave storage only through
/// retention, never through a user request (<c>SUB-021</c>).
/// </remarks>
[ApiController]
[Route("api/v1")]
[Produces("application/json")]
public sealed class SubmissionsController(ISubmissionService submissions) : ControllerBase
{
    /// <summary>
    /// <c>POST /assignments/{id}/submissions</c> — a new version of the work.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not idempotent by nature: every upload is a new version. Protection against a double click is
    /// the duplicate rule rather than a request key — a byte-identical file within the same assignment
    /// is refused (<c>API-015</c>, <c>SUB-028</c>).
    /// </para>
    /// <para>
    /// The form carries the file and nothing else. Organization, branch and category come from the
    /// assignment on the server; accepting them here would let a caller choose which branch's prefix
    /// to write into (<c>TEN-061</c>, <c>SEC-003</c>).
    /// </para>
    /// </remarks>
    [HttpPost("assignments/{id:guid}/submissions", Name = RouteNames.UploadSubmission)]
    [Authorize(Policy = MtfPolicies.Mentor)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(52_428_800)]
    [ProducesResponseType<SubmissionDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SubmissionDto>> UploadAsync(
        Guid id,
        IFormFile file,
        CancellationToken cancellationToken)
    {
        if (file is null)
        {
            throw new ValidationAppException("file", "Файл обязателен.");
        }

        // TEN-061: any attempt to supply scope through the form is refused rather than ignored.
        // Ignoring it would let a caller believe the server had honoured their value.
        var forbidden = Request.Form.Keys
            .Where(k => k is "organizationId" or "branchId" or "categoryId" or "assignmentId")
            .ToArray();

        if (forbidden.Length > 0)
        {
            throw new ValidationAppException(
                forbidden[0],
                "Арендный scope определяется сервером и не принимается в запросе.");
        }

        await using var content = file.OpenReadStream();

        var submission = await submissions.UploadAsync(
            id,
            new UploadedFile(content, file.FileName, file.ContentType, file.Length),
            cancellationToken);

        return CreatedAtRoute(
            RouteNames.ListSubmissions,
            new { id = submission.AssignmentId },
            submission);
    }

    /// <summary><c>GET /assignments/{id}/submissions</c> — the versions, newest first (<c>SUB-005</c>).</summary>
    [HttpGet("assignments/{id:guid}/submissions", Name = RouteNames.ListSubmissions)]
    [Authorize(Policy = MtfPolicies.Authenticated)]
    [ProducesResponseType<IReadOnlyList<SubmissionDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<SubmissionDto>>> ListAsync(
        Guid id,
        CancellationToken cancellationToken) =>
        Ok(await submissions.ListAsync(id, cancellationToken));

    /// <summary>
    /// <c>GET /submissions/{id}/download-url</c> — a presigned URL, valid for ten minutes.
    /// </summary>
    /// <remarks>
    /// An Organization Admin must have chosen a branch: issuing a file belongs to one branch and has
    /// no meaning as an aggregate (<c>TEN-065</c>).
    /// </remarks>
    [HttpGet("submissions/{id:guid}/download-url")]
    [Authorize(Policy = MtfPolicies.Authenticated)]
    [ProducesResponseType<FileUrlDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<FileUrlDto>> GetDownloadUrlAsync(Guid id, CancellationToken cancellationToken)
    {
        var url = await submissions.GetDownloadUrlAsync(id, cancellationToken);

        return NoStore(url);
    }

    /// <summary>
    /// <c>GET /submissions/{id}/preview-url</c> — PDF only.
    /// </summary>
    /// <remarks>
    /// A PPTX answers 404: Release 1.0 has no preview for it, so there is nothing to point at (17.5).
    /// </remarks>
    [HttpGet("submissions/{id:guid}/preview-url")]
    [Authorize(Policy = MtfPolicies.Authenticated)]
    [ProducesResponseType<FileUrlDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<FileUrlDto>> GetPreviewUrlAsync(Guid id, CancellationToken cancellationToken)
    {
        var url = await submissions.GetPreviewUrlAsync(id, cancellationToken);

        return NoStore(url);
    }

    /// <summary>
    /// A presigned URL is a bearer credential for its lifetime, so it must not sit in any cache
    /// (<c>API-017</c>, <c>SEC-020</c>).
    /// </summary>
    private ActionResult<FileUrlDto> NoStore(FileUrlDto url)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";

        return Ok(url);
    }
}
