using MentorTaskFlow.Domain.Identity;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Infrastructure.Identity;
using MentorTaskFlow.Infrastructure.Options;
using MentorTaskFlow.Infrastructure.Persistence;
using MentorTaskFlow.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MentorTaskFlow.IntegrationTests.Identity;

/// <summary>
/// Provisioning of the first tenant (TZ 32.6).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class BootstrapTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly BootstrapOptions Options = new()
    {
        OrganizationName = "SoftClub Academy",
        OrganizationSlug = "softclub-academy",
        HeadOfficeName = "Главный офис",
        HeadOfficeCode = "HQ",
        HeadOfficeTimeZone = "Asia/Dushanbe",
        AdminEmail = "admin@mentortaskflow.test",
    };

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// <c>DEPLOY-030</c>: organization, head office, administrator and a set-password token, all in
    /// one transaction.
    /// </summary>
    [Fact]
    public async Task Provisioning_creates_the_whole_minimal_tenant()
    {
        var result = await ProvisionAsync();

        result.Provisioned.ShouldBeTrue();
        result.SetPasswordLink.ShouldNotBeNullOrWhiteSpace();

        await using var context = fixture.CreateContext(suppressTenantFilter: true);

        var organization = await context.Organizations.SingleAsync();
        organization.Name.ShouldBe("SoftClub Academy");
        organization.Slug.ShouldBe("softclub-academy");
        organization.IsActive.ShouldBeTrue();

        var branch = await context.Branches.SingleAsync();
        branch.OrganizationId.ShouldBe(organization.Id);
        branch.Code.ShouldBe("HQ");

        // BRN-028: «Organization without a Branch» is never observable, so the head office must exist
        // and must carry the flag from the outset.
        branch.IsHeadOffice.ShouldBeTrue();

        var admin = await context.Users.SingleAsync();
        admin.Role.ShouldBe(UserRole.Admin);

        // DEPLOY-031: an Organization Admin, not an administrator of the head office. A branch-bound
        // admin could not create a second branch, leaving the organization stuck on one.
        admin.AdminScope.ShouldBe(AdminScope.Organization);
        admin.BranchId.ShouldBeNull();
        admin.CategoryId.ShouldBeNull();

        // AUTH-019 and DEPLOY-025: no generated password, no default credentials. The account cannot
        // sign in until the link is used.
        admin.PasswordHash.ShouldBeNull();

        var token = await context.UserSecurityTokens.SingleAsync();
        token.Purpose.ShouldBe(SecurityTokenPurpose.SetPassword);
        token.UserId.ShouldBe(admin.Id);
    }

    /// <summary>The link points at the SPA and carries the token as a query parameter (<c>AUTH-020</c>).</summary>
    [Fact]
    public async Task The_set_password_link_targets_the_configured_application()
    {
        var result = await ProvisionAsync();

        var uri = new Uri(result.SetPasswordLink!);
        uri.AbsolutePath.ShouldBe("/set-password");
        uri.Query.ShouldContain("token=");
    }

    /// <summary>
    /// <c>DEPLOY-022</c> and <c>DEPLOY-032</c>: idempotent. Every deploy after the first must skip
    /// rather than provision a second tenant.
    /// </summary>
    [Fact]
    public async Task A_second_run_changes_nothing()
    {
        var first = await ProvisionAsync();
        var second = await ProvisionAsync();

        first.Provisioned.ShouldBeTrue();
        second.Provisioned.ShouldBeFalse();
        second.SkipReason.ShouldNotBeNullOrWhiteSpace();

        await using var context = fixture.CreateContext(suppressTenantFilter: true);
        (await context.Organizations.CountAsync()).ShouldBe(1);
        (await context.Branches.CountAsync()).ShouldBe(1);
        (await context.Users.CountAsync()).ShouldBe(1);
    }

    /// <summary>Incomplete configuration is a skip, not a crash — the step is simply not requested.</summary>
    [Fact]
    public async Task Incomplete_configuration_skips_provisioning()
    {
        var result = await ProvisionAsync(new BootstrapOptions { OrganizationName = "SoftClub Academy" });

        result.Provisioned.ShouldBeFalse();

        await using var context = fixture.CreateContext(suppressTenantFilter: true);
        (await context.Organizations.CountAsync()).ShouldBe(0);
    }

    /// <summary>
    /// A malformed slug aborts the whole transaction — no organization, no branch, no orphaned
    /// administrator (<c>DEPLOY-030</c>).
    /// </summary>
    [Fact]
    public async Task Invalid_input_leaves_no_partial_tenant()
    {
        var invalid = new BootstrapOptions
        {
            OrganizationName = Options.OrganizationName,
            OrganizationSlug = "Not A Slug",
            HeadOfficeName = Options.HeadOfficeName,
            HeadOfficeCode = Options.HeadOfficeCode,
            HeadOfficeTimeZone = Options.HeadOfficeTimeZone,
            AdminEmail = Options.AdminEmail,
        };

        await Should.ThrowAsync<Domain.Common.DomainException>(ProvisionAsync(invalid));

        await using var context = fixture.CreateContext(suppressTenantFilter: true);
        (await context.Organizations.CountAsync()).ShouldBe(0);
        (await context.Branches.CountAsync()).ShouldBe(0);
        (await context.Users.CountAsync()).ShouldBe(0);
    }

    private Task<BootstrapResult> ProvisionAsync(BootstrapOptions? options = null)
    {
        var context = fixture.CreateContext(suppressTenantFilter: true);
        var authOptions = Microsoft.Extensions.Options.Options.Create(new AuthOptions
        {
            JwtSigningKey = MentorTaskFlowApiFactory.TestSigningKey,
            AppBaseUrl = MentorTaskFlowApiFactory.AllowedOrigin,
        });

        var clock = new FixedClock(new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero));

        var authService = new AuthService(
            context,
            new Pbkdf2PasswordHasher(),
            new SecureTokenService(),
            new JwtTokenService(authOptions, clock),
            new NoOpTokenVersionValidator(),
            new Application.Common.Security.PasswordPolicy(new EmbeddedCommonPasswordCatalog()),
            authOptions,
            clock);

        var provisioner = new BootstrapProvisioner(
            context,
            authService,
            Microsoft.Extensions.Options.Options.Create(options ?? Options),
            clock,
            NullLogger<BootstrapProvisioner>.Instance);

        return provisioner.ProvisionAsync(CancellationToken.None);
    }

    private sealed class FixedClock(DateTimeOffset now) : Application.Common.Abstractions.IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    /// <summary>Provisioning issues no access token, so the version cache is never consulted.</summary>
    private sealed class NoOpTokenVersionValidator : Application.Common.Security.ITokenVersionValidator
    {
        public Task<Application.Common.Security.TokenVersionCheck> CheckAsync(
            Guid userId, int presentedTokenVersion, CancellationToken cancellationToken) =>
            Task.FromResult(Application.Common.Security.TokenVersionCheck.Valid);

        public void Invalidate(Guid userId)
        {
        }
    }
}
