using MentorTaskFlow.Api.Authorization;
using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Contracts.Categories;
using MentorTaskFlow.Contracts.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MentorTaskFlow.Api.Controllers;

/// <summary>
/// Categories and their settings (Приложение D.3, TZ 15.3, 39.4).
/// </summary>
/// <remarks>
/// <para>
/// A category belongs to exactly one branch of exactly one organization, and both are immutable.
/// Moving a category between branches is unsupported — it would distort the historical data of both —
/// so the same effect is achieved by creating a new category and transferring users (<c>CAT-022</c>).
/// </para>
/// <para>
/// There is no <c>DELETE</c>. Every foreign key to a category is <c>ON DELETE RESTRICT</c>, and
/// deactivation is the supported path (<c>CAT-013</c>).
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/categories")]
[Produces("application/json")]
public sealed class CategoriesController(ICategoryService categoryService) : ControllerBase
{
    /// <summary>
    /// <c>GET /categories</c> — scoped to what the caller may see (<c>CAT-007</c>).
    /// </summary>
    /// <remarks>
    /// Open to any authenticated user because the reach differs by role rather than by permission: a
    /// Lead and a Mentor legitimately call this and receive exactly their own category.
    /// </remarks>
    [HttpGet]
    [Authorize(Policy = MtfPolicies.Authenticated)]
    [ProducesResponseType<PagedResult<CategoryDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<CategoryDto>>> ListAsync(
        [FromQuery] CategoryListQuery query,
        CancellationToken cancellationToken) =>
        Ok(await categoryService.ListAsync(query, cancellationToken));

    [HttpGet("{id:guid}", Name = RouteNames.GetCategory)]
    [Authorize(Policy = MtfPolicies.Authenticated)]
    [ProducesResponseType<CategoryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CategoryDto>> GetAsync(Guid id, CancellationToken cancellationToken) =>
        Ok(await categoryService.GetAsync(id, cancellationToken));

    /// <summary>
    /// <c>POST /categories</c> — either administrative contour.
    /// </summary>
    /// <remarks>
    /// A Branch Admin creates in their own branch and must not send <c>X-MTF-Branch-Id</c>; an
    /// Organization Admin must send it, because a branch-scoped mutation without a chosen branch has
    /// no sane default (<c>CAT-025</c>, <c>TEN-033</c>).
    /// </remarks>
    [HttpPost]
    [Authorize(Policy = MtfPolicies.AnyAdmin)]
    [ProducesResponseType<CategoryDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CategoryDto>> CreateAsync(
        CreateCategoryRequest request,
        CancellationToken cancellationToken)
    {
        var category = await categoryService.CreateAsync(request, cancellationToken);

        return CreatedAtRoute(RouteNames.GetCategory, new { id = category.Id }, category);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = MtfPolicies.AnyAdmin)]
    [ProducesResponseType<CategoryDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<CategoryDto>> UpdateAsync(
        Guid id,
        UpdateCategoryRequest request,
        CancellationToken cancellationToken) =>
        Ok(await categoryService.UpdateAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/activate")]
    [Authorize(Policy = MtfPolicies.AnyAdmin)]
    [ProducesResponseType<CategoryDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<CategoryDto>> ActivateAsync(
        Guid id,
        CategoryActionRequest request,
        CancellationToken cancellationToken) =>
        Ok(await categoryService.ActivateAsync(id, request, cancellationToken));

    /// <summary>
    /// <c>POST /categories/{id}/deactivate</c>.
    /// </summary>
    /// <remarks>
    /// Blocks writes in the category's contour but changes no assignment status and cancels nothing.
    /// Existing tasks stay where they are and can only be cancelled or left to go overdue
    /// (<c>CAT-010</c>, <c>CAT-011</c>).
    /// </remarks>
    [HttpPost("{id:guid}/deactivate")]
    [Authorize(Policy = MtfPolicies.AnyAdmin)]
    [ProducesResponseType<CategoryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CategoryDto>> DeactivateAsync(
        Guid id,
        DeactivateCategoryRequest request,
        CancellationToken cancellationToken) =>
        Ok(await categoryService.DeactivateAsync(id, request, cancellationToken));

    /// <summary>
    /// <c>GET /categories/{id}/settings</c> — readable by the Lead and Mentor of the category.
    /// </summary>
    /// <remarks>
    /// They need the time zone and the late-submission policy to render deadlines honestly: every
    /// date in the interface is shown in the category's zone, never the browser's (<c>CAT-006</c>,
    /// <c>UX-003</c>).
    /// </remarks>
    [HttpGet("{id:guid}/settings")]
    [Authorize(Policy = MtfPolicies.Authenticated)]
    [ProducesResponseType<CategorySettingsDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<CategorySettingsDto>> GetSettingsAsync(Guid id, CancellationToken cancellationToken) =>
        Ok(await categoryService.GetSettingsAsync(id, cancellationToken));

    [HttpPut("{id:guid}/settings")]
    [Authorize(Policy = MtfPolicies.AnyAdmin)]
    [ProducesResponseType<CategorySettingsDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<CategorySettingsDto>> UpdateSettingsAsync(
        Guid id,
        UpdateCategorySettingsRequest request,
        CancellationToken cancellationToken) =>
        Ok(await categoryService.UpdateSettingsAsync(id, request, cancellationToken));
}
