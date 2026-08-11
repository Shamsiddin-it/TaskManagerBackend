using MentorTaskFlow.Contracts.Categories;
using MentorTaskFlow.Contracts.Common;

namespace MentorTaskFlow.Application.Common.Abstractions;

/// <summary>Categories and their settings (TZ 15.3, 39.4, Приложение D.3).</summary>
public interface ICategoryService
{
    /// <summary>
    /// Lists categories within the caller's reach.
    /// </summary>
    /// <remarks>
    /// An Organization Admin sees the selected branch, or every branch when none is selected — and in
    /// that mode each row carries its branch, because two categories may share a name and be
    /// distinguishable only by it (<c>TEN-073</c>). A Branch Admin sees their own branch; a Lead or
    /// Mentor sees exactly their own category (<c>CAT-007</c>).
    /// </remarks>
    Task<PagedResult<CategoryDto>> ListAsync(CategoryListQuery query, CancellationToken cancellationToken);

    Task<CategoryDto> GetAsync(Guid categoryId, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a category together with its settings.
    /// </summary>
    /// <remarks>
    /// Both rows are written in one transaction: a category without settings does not exist
    /// (<c>CAT-014</c>), and the settings inherit <c>Branch.TimeZoneId</c> (<c>CAT-023</c>).
    /// </remarks>
    Task<CategoryDto> CreateAsync(CreateCategoryRequest request, CancellationToken cancellationToken);

    Task<CategoryDto> UpdateAsync(Guid categoryId, UpdateCategoryRequest request, CancellationToken cancellationToken);

    Task<CategoryDto> ActivateAsync(Guid categoryId, CategoryActionRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Deactivates a category.
    /// </summary>
    /// <remarks>
    /// Blocks writes in its contour but changes no assignment status and cancels nothing: existing
    /// tasks stay where they are and can only be cancelled or left to go overdue (<c>CAT-010</c>,
    /// <c>CAT-011</c>).
    /// </remarks>
    Task<CategoryDto> DeactivateAsync(Guid categoryId, DeactivateCategoryRequest request, CancellationToken cancellationToken);

    Task<CategorySettingsDto> GetSettingsAsync(Guid categoryId, CancellationToken cancellationToken);

    Task<CategorySettingsDto> UpdateSettingsAsync(
        Guid categoryId,
        UpdateCategorySettingsRequest request,
        CancellationToken cancellationToken);
}
