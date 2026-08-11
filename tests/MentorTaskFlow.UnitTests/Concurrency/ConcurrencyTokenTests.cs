using MentorTaskFlow.Application.Common.Concurrency;
using MentorTaskFlow.Application.Common.Exceptions;

namespace MentorTaskFlow.UnitTests.Concurrency;

/// <summary><c>API-020</c>: the token is opaque on the wire and round-trips exactly.</summary>
public sealed class ConcurrencyTokenTests
{
    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(42u)]
    [InlineData(uint.MaxValue)]
    public void A_token_round_trips(uint xmin)
    {
        ConcurrencyToken.Decode(ConcurrencyToken.Encode(xmin)).ShouldBe(xmin);
    }

    /// <summary>
    /// Encoded, not handed over raw. The format is an implementation detail a client has no right to
    /// parse — and a visible integer invites "helpfully" incrementing it, which turns optimistic
    /// concurrency into none.
    /// </summary>
    [Fact]
    public void The_token_does_not_expose_the_raw_number()
    {
        ConcurrencyToken.Encode(12345u).ShouldNotBe("12345");
    }

    [Fact]
    public void Distinct_versions_produce_distinct_tokens()
    {
        ConcurrencyToken.Encode(1u).ShouldNotBe(ConcurrencyToken.Encode(2u));
    }

    /// <summary>
    /// Absence is 400, not 409: the client sent nothing to compare, so telling it to reload and retry
    /// would send it round a loop that fails identically forever (<c>API-020</c>).
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_token_is_a_validation_failure(string? token)
    {
        var exception = Should.Throw<ValidationAppException>(() => ConcurrencyToken.Decode(token));

        exception.Errors.ShouldContainKey("concurrencyToken");
    }

    [Theory]
    [InlineData("not-base64url!!")]
    [InlineData("bm90LWEtbnVtYmVy")] // Base64Url of "not-a-number"
    [InlineData("LTE")]              // Base64Url of "-1": xmin is unsigned
    public void A_malformed_token_is_a_validation_failure(string token)
    {
        Should.Throw<ValidationAppException>(() => ConcurrencyToken.Decode(token));
    }

    [Fact]
    public void The_field_name_in_the_error_is_configurable()
    {
        var exception = Should.Throw<ValidationAppException>(
            () => ConcurrencyToken.Decode(null, "branchConcurrencyToken"));

        exception.Errors.ShouldContainKey("branchConcurrencyToken");
    }
}
