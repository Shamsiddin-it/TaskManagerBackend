using MentorTaskFlow.Domain.Common;
using MentorTaskFlow.Domain.Identity;

namespace MentorTaskFlow.UnitTests.Telegram;

/// <summary>The single-use bind token (TZ 10.11, 19.2, 19.3).</summary>
public sealed class TelegramBindTokenTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_new_token_expires_in_fifteen_minutes()
    {
        var token = Issue();

        token.ExpiresAt.ShouldBe(Now.AddMinutes(15));
        token.UsedAt.ShouldBeNull();
        token.IsRedeemable(Now).ShouldBeTrue();
    }

    [Fact]
    public void Redeeming_marks_the_token_spent()
    {
        var token = Issue();

        token.Redeem(Now.AddMinutes(1));

        token.UsedAt.ShouldBe(Now.AddMinutes(1));
        token.IsRedeemable(Now.AddMinutes(2)).ShouldBeFalse();
    }

    /// <summary>Single-use even inside the lifetime (<c>TG-007</c>).</summary>
    [Fact]
    public void A_spent_token_cannot_be_redeemed_again()
    {
        var token = Issue();
        token.Redeem(Now);

        Should.Throw<DomainException>(() => token.Redeem(Now))
            .Code.ShouldBe(DomainErrorCodes.TelegramBindTokenInvalid);
    }

    [Fact]
    public void An_expired_token_cannot_be_redeemed()
    {
        var token = Issue();

        token.IsRedeemable(Now.AddMinutes(16)).ShouldBeFalse();
        Should.Throw<DomainException>(() => token.Redeem(Now.AddMinutes(16)));
    }

    /// <summary>
    /// <c>TG-006</c>: issuing a new token retires the previous one, so a link that leaked stops
    /// working the moment its owner notices.
    /// </summary>
    [Fact]
    public void Invalidating_retires_an_unused_token()
    {
        var token = Issue();

        token.Invalidate(Now.AddMinutes(1));

        token.IsRedeemable(Now.AddMinutes(2)).ShouldBeFalse();
    }

    /// <summary>Invalidation must not rewrite when a token was actually used and when it was retired.</summary>
    [Fact]
    public void Invalidating_a_spent_token_keeps_the_original_moment()
    {
        var token = Issue();
        token.Redeem(Now.AddMinutes(1));

        token.Invalidate(Now.AddMinutes(5));

        token.UsedAt.ShouldBe(Now.AddMinutes(1));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_token_without_a_hash_is_refused(string hash) =>
        Should.Throw<DomainException>(() => TelegramBindToken.Issue(Guid.CreateVersion7(), hash, Now));

    [Fact]
    public void A_token_without_a_user_is_refused() =>
        Should.Throw<DomainException>(() => TelegramBindToken.Issue(Guid.Empty, new string('a', 64), Now));

    private static TelegramBindToken Issue() =>
        TelegramBindToken.Issue(Guid.CreateVersion7(), new string('a', 64), Now);
}
