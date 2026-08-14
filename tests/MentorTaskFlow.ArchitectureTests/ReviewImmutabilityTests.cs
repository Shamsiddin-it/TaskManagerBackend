using System.Reflection;
using MentorTaskFlow.Contracts.Reviews;
using MentorTaskFlow.Domain.Reviews;

namespace MentorTaskFlow.ArchitectureTests;

/// <summary>
/// <c>REV-020</c>: a review is never edited or deleted, by any role including Admin.
/// </summary>
/// <remarks>
/// A verdict that could be rewritten afterwards would make the history of a task useless in exactly
/// the dispute it exists to settle. The rule is enforced by the absence of a write path, which is the
/// kind of thing a later refactor removes by accident.
/// </remarks>
public sealed class ReviewImmutabilityTests
{
    [Fact]
    public void A_review_cannot_be_modified()
    {
        typeof(Review).GetProperty("UpdatedAt").ShouldBeNull();

        foreach (var property in typeof(Review).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            (property.SetMethod?.IsPublic ?? false).ShouldBeFalse(
                $"Review.{property.Name} must not be settable: a decision is never rewritten (REV-020).");
        }
    }

    /// <summary>Append-only entities carry no concurrency token — one would imply an intent to update.</summary>
    [Fact]
    public void A_review_has_no_concurrency_token() =>
        typeof(Review).GetProperty("ConcurrencyToken").ShouldBeNull();

    /// <summary>
    /// The response carries no token either: offering one would invite a client to attempt the update
    /// that does not exist.
    /// </summary>
    [Fact]
    public void The_response_offers_no_concurrency_token() =>
        typeof(ReviewDto).GetProperty("ConcurrencyToken").ShouldBeNull();

    /// <summary>
    /// <c>REV-006</c>: the reviewer is taken from the token. A field for it in the request would be a
    /// way to attribute a decision to somebody else.
    /// </summary>
    [Fact]
    public void The_request_does_not_accept_a_reviewer() =>
        typeof(CreateReviewRequest).GetProperty("ReviewerId").ShouldBeNull();

    [Fact]
    public void The_two_decisions_match_the_specification() =>
        Enum.GetNames<ReviewDecision>().ShouldBe(["Approved", "NeedsRework"], ignoreOrder: true);
}
