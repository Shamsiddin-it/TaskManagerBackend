namespace MentorTaskFlow.Contracts.Common;

/// <summary>Pagination bounds. Source of truth: Приложение L.7 (<c>API-011</c>).</summary>
public static class PaginationLimits
{
    public const int DefaultPage = 1;
    public const int DefaultPageSize = 20;
    public const int MinPageSize = 1;
    public const int MaxPageSize = 100;
}
