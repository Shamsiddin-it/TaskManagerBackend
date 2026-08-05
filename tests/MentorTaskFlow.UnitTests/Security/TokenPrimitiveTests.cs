using MentorTaskFlow.Domain.Common;
using MentorTaskFlow.Domain.Identity;
using MentorTaskFlow.Infrastructure.Identity;

namespace MentorTaskFlow.UnitTests.Security;

/// <summary>The cryptographic primitives and the token entities behind TZ 16.2 and 16.5.</summary>
public sealed class TokenPrimitiveTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserId = Guid.CreateVersion7();

    private readonly Pbkdf2PasswordHasher _hasher = new();
    private readonly SecureTokenService _tokens = new();

    // -----------------------------------------------------------------
    // Password hashing
    // -----------------------------------------------------------------

    [Fact]
    public void A_hashed_password_verifies()
    {
        var hash = _hasher.Hash("Karimov2026Task");

        _hasher.Verify("Karimov2026Task", hash).ShouldBeTrue();
        _hasher.Verify("karimov2026task", hash).ShouldBeFalse();
    }

    /// <summary>
    /// A random salt per call, so two identical passwords do not share a hash. Without it, a single
    /// leaked hash would reveal every account using that password.
    /// </summary>
    [Fact]
    public void The_same_password_hashes_differently_each_time()
    {
        var first = _hasher.Hash("Karimov2026Task");
        var second = _hasher.Hash("Karimov2026Task");

        first.ShouldNotBe(second);
        _hasher.Verify("Karimov2026Task", first).ShouldBeTrue();
        _hasher.Verify("Karimov2026Task", second).ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64!!")]
    [InlineData("AAAA")]
    public void A_malformed_stored_hash_verifies_to_false_rather_than_throwing(string storedHash)
    {
        // A throw here would surface as a 500 on the login path and would tell an attacker that the
        // account exists but its record is broken.
        _hasher.Verify("Karimov2026Task", storedHash).ShouldBeFalse();
    }

    // -----------------------------------------------------------------
    // Opaque tokens
    // -----------------------------------------------------------------

    [Fact]
    public void A_generated_token_is_256_bits_of_base64url_with_a_matching_hash()
    {
        var (plain, hash) = _tokens.Generate();

        // 32 bytes → 43 Base64Url characters, no padding.
        plain.Length.ShouldBe(43);
        plain.ShouldNotContain("=");
        plain.ShouldNotContain("+");
        plain.ShouldNotContain("/");

        hash.Length.ShouldBe(64);
        hash.ShouldBe(hash.ToLowerInvariant());
        hash.ShouldBe(_tokens.HashToken(plain));
    }

    [Fact]
    public void Two_generated_tokens_differ()
    {
        var (first, _) = _tokens.Generate();
        var (second, _) = _tokens.Generate();

        first.ShouldNotBe(second);
    }

    [Fact]
    public void Fixed_time_comparison_matches_ordinary_equality()
    {
        var (_, hash) = _tokens.Generate();

        _tokens.FixedTimeEquals(hash, hash).ShouldBeTrue();
        _tokens.FixedTimeEquals(hash, _tokens.HashToken("other")).ShouldBeFalse();
        _tokens.FixedTimeEquals(hash, hash[..32]).ShouldBeFalse();
    }

    // -----------------------------------------------------------------
    // RefreshToken
    // -----------------------------------------------------------------

    [Fact]
    public void A_freshly_issued_refresh_token_is_active_until_it_expires()
    {
        var token = RefreshToken.IssueNewFamily(UserId, "hash", TimeSpan.FromDays(14), "10.0.0.1", Now);

        token.IsActive(Now).ShouldBeTrue();
        token.IsActive(Now.AddDays(14).AddSeconds(1)).ShouldBeFalse();
    }

    [Fact]
    public void Rotation_links_the_replacement_and_records_the_reason()
    {
        var token = RefreshToken.IssueNewFamily(UserId, "hash", TimeSpan.FromDays(14), null, Now);
        var replacementId = Guid.CreateVersion7();

        token.Rotate(replacementId, "10.0.0.1", Now);

        token.RevokedAt.ShouldBe(Now);
        token.ReplacedByTokenId.ShouldBe(replacementId);
        token.ReasonRevoked.ShouldBe(RefreshTokenRevocationReason.Rotated);
        token.IsActive(Now).ShouldBeFalse();
    }

    /// <summary>
    /// Revocation is idempotent and keeps the <b>first</b> reason. Overwriting it would let a later
    /// rotation mask an earlier <c>ReuseDetected</c> — erasing the evidence of the one event the field
    /// exists to record.
    /// </summary>
    [Fact]
    public void Re_revoking_preserves_the_original_reason()
    {
        var token = RefreshToken.IssueNewFamily(UserId, "hash", TimeSpan.FromDays(14), null, Now);

        token.Revoke(RefreshTokenRevocationReason.ReuseDetected, null, Now);
        token.Revoke(RefreshTokenRevocationReason.Logout, null, Now.AddMinutes(1));

        token.ReasonRevoked.ShouldBe(RefreshTokenRevocationReason.ReuseDetected);
        token.RevokedAt.ShouldBe(Now);
    }

    [Fact]
    public void Family_membership_is_preserved_across_rotation()
    {
        var first = RefreshToken.IssueNewFamily(UserId, "a", TimeSpan.FromDays(14), null, Now);
        var second = RefreshToken.IssueInFamily(UserId, "b", first.FamilyId, TimeSpan.FromDays(14), null, Now);

        second.FamilyId.ShouldBe(first.FamilyId);
    }

    // -----------------------------------------------------------------
    // UserSecurityToken
    // -----------------------------------------------------------------

    [Fact]
    public void A_security_token_can_be_redeemed_exactly_once()
    {
        var token = UserSecurityToken.Issue(
            UserId, SecurityTokenPurpose.SetPassword, "hash", TimeSpan.FromHours(24), null, Now);

        token.TryRedeem(Now).ShouldBeTrue();
        token.TryRedeem(Now).ShouldBeFalse();
        token.UsedAt.ShouldBe(Now);
    }

    [Fact]
    public void An_expired_security_token_cannot_be_redeemed()
    {
        var token = UserSecurityToken.Issue(
            UserId, SecurityTokenPurpose.ResetPassword, "hash", TimeSpan.FromMinutes(30), null, Now);

        token.TryRedeem(Now.AddMinutes(31)).ShouldBeFalse();
        token.UsedAt.ShouldBeNull();
    }

    /// <summary><c>AUTH-017</c>: issuing a new link retires the previous one — exactly one live link.</summary>
    [Fact]
    public void An_invalidated_security_token_cannot_be_redeemed()
    {
        var token = UserSecurityToken.Issue(
            UserId, SecurityTokenPurpose.SetPassword, "hash", TimeSpan.FromHours(24), null, Now);

        token.Invalidate(Now);

        token.IsActive(Now).ShouldBeFalse();
        token.TryRedeem(Now).ShouldBeFalse();
    }

    [Fact]
    public void Invalidating_a_redeemed_token_does_not_rewrite_its_history()
    {
        var token = UserSecurityToken.Issue(
            UserId, SecurityTokenPurpose.SetPassword, "hash", TimeSpan.FromHours(24), null, Now);

        token.TryRedeem(Now);
        token.Invalidate(Now.AddMinutes(1));

        token.UsedAt.ShouldBe(Now);
        token.InvalidatedAt.ShouldBeNull();
    }

    [Fact]
    public void A_token_without_a_user_is_rejected()
    {
        Should.Throw<DomainException>(() => UserSecurityToken.Issue(
            Guid.Empty, SecurityTokenPurpose.SetPassword, "hash", TimeSpan.FromHours(24), null, Now));
    }
}
