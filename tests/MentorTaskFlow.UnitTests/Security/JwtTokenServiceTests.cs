using System.IdentityModel.Tokens.Jwt;
using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Application.Common.Tenancy;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Domain.Users;
using MentorTaskFlow.Infrastructure.Identity;
using MentorTaskFlow.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace MentorTaskFlow.UnitTests.Security;

/// <summary><c>AUTH-031</c>: the claim set is decided strictly by role and administrative contour.</summary>
public sealed class JwtTokenServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid BranchId = Guid.CreateVersion7();
    private static readonly Guid CategoryId = Guid.CreateVersion7();

    private readonly JwtTokenService _service = new(
        Options.Create(new AuthOptions
        {
            JwtSigningKey = "unit-test-signing-key-0123456789-abcdefghijklmnop",
            JwtIssuer = "mentortaskflow-tests",
            JwtAudience = "mentortaskflow-tests-api",
        }),
        new FixedClock(Now));

    [Fact]
    public void An_organization_admin_carries_neither_branch_nor_category()
    {
        var claims = Claims(User.CreateOrganizationAdmin(Org, "Админ", "oa@mtf.test", Now));

        claims[MtfClaimTypes.Role].ShouldBe("Admin");
        claims[MtfClaimTypes.AdminScope].ShouldBe("Organization");
        claims[MtfClaimTypes.OrganizationId].ShouldBe(Org.ToString());

        // Absent, not null. A null written into the token would make the AUTH-032 shape check
        // meaningless, because absence would no longer mean absence.
        claims.ShouldNotContainKey(MtfClaimTypes.BranchId);
        claims.ShouldNotContainKey(MtfClaimTypes.CategoryId);
    }

    [Fact]
    public void A_branch_admin_carries_a_branch_but_no_category()
    {
        var claims = Claims(User.CreateBranchAdmin(Org, BranchId, "Админ филиала", "ba@mtf.test", Now));

        claims[MtfClaimTypes.AdminScope].ShouldBe("Branch");
        claims[MtfClaimTypes.BranchId].ShouldBe(BranchId.ToString());
        claims.ShouldNotContainKey(MtfClaimTypes.CategoryId);
    }

    [Theory]
    [InlineData(UserRole.Lead)]
    [InlineData(UserRole.Mentor)]
    public void Lead_and_mentor_carry_branch_and_category_and_no_admin_scope(UserRole role)
    {
        var user = role is UserRole.Lead
            ? User.CreateLead(Org, BranchId, CategoryId, "Лид", "lead@mtf.test", Now)
            : User.CreateMentor(Org, BranchId, CategoryId, "Ментор", "mentor@mtf.test", Now);

        var claims = Claims(user);

        claims[MtfClaimTypes.Role].ShouldBe(role.ToString());
        claims[MtfClaimTypes.BranchId].ShouldBe(BranchId.ToString());
        claims[MtfClaimTypes.CategoryId].ShouldBe(CategoryId.ToString());
        claims.ShouldNotContainKey(MtfClaimTypes.AdminScope);
    }

    /// <summary><c>AUTH-033</c>: <c>org_id</c> is present in every token of every user type.</summary>
    [Fact]
    public void Every_token_carries_the_organization()
    {
        User[] users =
        [
            User.CreateOrganizationAdmin(Org, "Админ", "oa@mtf.test", Now),
            User.CreateBranchAdmin(Org, BranchId, "Админ филиала", "ba@mtf.test", Now),
            User.CreateLead(Org, BranchId, CategoryId, "Лид", "lead@mtf.test", Now),
            User.CreateMentor(Org, BranchId, CategoryId, "Ментор", "mentor@mtf.test", Now),
        ];

        foreach (var user in users)
        {
            Claims(user).ShouldContainKey(MtfClaimTypes.OrganizationId);
        }
    }

    /// <summary>
    /// <c>tv</c> is what actually revokes a token: a stale value is refused within the cache window
    /// (<c>AUTH-026</c>).
    /// </summary>
    [Fact]
    public void The_token_version_travels_in_the_token()
    {
        var user = User.CreateMentor(Org, BranchId, CategoryId, "Ментор", "m@mtf.test", Now);
        Claims(user)[MtfClaimTypes.TokenVersion].ShouldBe("0");

        user.BumpTokenVersion(Now);
        Claims(user)[MtfClaimTypes.TokenVersion].ShouldBe("1");
    }

    [Fact]
    public void The_token_expires_after_the_configured_lifetime()
    {
        var user = User.CreateMentor(Org, BranchId, CategoryId, "Ментор", "m@mtf.test", Now);

        var token = _service.Issue(user);

        // AUTH-001: 15 minutes. Together with the 30-second version cache this is the outer bound of
        // the revocation guarantee stated in section 16.7.
        token.ExpiresAt.ShouldBe(Now.AddMinutes(15));
    }

    /// <summary>Each token gets its own <c>jti</c>, so two issues are never the same string.</summary>
    [Fact]
    public void Two_tokens_for_the_same_user_differ()
    {
        var user = User.CreateMentor(Org, BranchId, CategoryId, "Ментор", "m@mtf.test", Now);

        _service.Issue(user).Value.ShouldNotBe(_service.Issue(user).Value);
    }

    private Dictionary<string, string> Claims(User user)
    {
        var token = new JwtSecurityTokenHandler().ReadJwtToken(_service.Issue(user).Value);
        return token.Claims.ToDictionary(c => c.Type, c => c.Value);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
