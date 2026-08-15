using MentorTaskFlow.Contracts.Analytics;

namespace MentorTaskFlow.Application.Common.Abstractions;

/// <summary>The result of a request, and whether it cost a call to the provider.</summary>
/// <remarks>
/// <c>AI-013</c> answers 200 for a report that already existed and 201 for one generated now, so the
/// service has to say which happened — the controller cannot tell from the payload.
/// </remarks>
public sealed record AiSummaryResult(AiSummaryDto Summary, bool WasCreated);

/// <summary>
/// AI summaries over the analytics of TZ 21 (TZ 22).
/// </summary>
/// <remarks>
/// The model never computes a figure: it is handed aggregates that the system already calculated and
/// asked for prose (22.1). That is what keeps the numbers on the analytics page true when the provider
/// is unavailable, wrong, or turned off entirely.
/// </remarks>
public interface IAiSummaryService
{
    Task<AiSummaryResult> GenerateAsync(AiSummaryRequest request, CancellationToken cancellationToken);
}
