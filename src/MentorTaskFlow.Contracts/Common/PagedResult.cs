namespace MentorTaskFlow.Contracts.Common;

/// <summary>
/// The single pagination envelope for every list endpoint (<c>API-004</c>, TZ 23.3).
/// </summary>
/// <remarks>
/// <paramref name="TotalCount"/> is computed with the same filter as <paramref name="Items"/> and
/// therefore honours the isolation rules of TZ section 9: a caller can never learn from the counter
/// that objects exist outside their scope (<c>API-012</c>, <c>TEN-002</c>).
/// </remarks>
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public static PagedResult<T> Empty(int page, int pageSize) =>
        new([], page, pageSize, 0);
}
