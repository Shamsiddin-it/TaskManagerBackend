using MentorTaskFlow.Domain.Common;
using MentorTaskFlow.Domain.Tenancy;

namespace MentorTaskFlow.UnitTests.Tenancy;

public sealed class OrganizationAndBranchTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Org = Guid.CreateVersion7();

    [Theory]
    [InlineData("softclub-academy")]
    [InlineData("mtf2026")]
    [InlineData("a1")]
    public void Valid_slugs_are_accepted(string slug)
    {
        Organization.Provision("SoftClub Academy", slug, Now).Slug.ShouldBe(slug);
    }

    [Theory]
    [InlineData("SoftClub")]        // uppercase
    [InlineData("soft_club")]       // underscore
    [InlineData("-softclub")]       // leading hyphen
    [InlineData("softclub-")]       // trailing hyphen
    [InlineData("soft--club")]      // doubled hyphen
    [InlineData("a")]               // shorter than 2
    public void Invalid_slugs_are_rejected(string slug)
    {
        Should.Throw<DomainException>(() => Organization.Provision("SoftClub Academy", slug, Now))
            .Code.ShouldBe(DomainErrorCodes.ValidationFailed);
    }

    /// <summary><c>ORG-020</c>: the slug is immutable — renaming exposes no way to change it.</summary>
    [Fact]
    public void Renaming_recomputes_the_normalized_name_and_leaves_the_slug_alone()
    {
        var organization = Organization.Provision("SoftClub Academy", "softclub-academy", Now);

        organization.Rename("SoftClub Group", Now);

        organization.Name.ShouldBe("SoftClub Group");
        organization.NormalizedName.ShouldBe("SOFTCLUB GROUP");
        organization.Slug.ShouldBe("softclub-academy");
    }

    [Theory]
    [InlineData("HQ")]
    [InlineData("KHJ")]
    [InlineData("BR-01")]
    public void Valid_branch_codes_are_accepted(string code)
    {
        Branch.Create(Org, "Филиал Худжанд", code, null, "Asia/Dushanbe", Now).Code.ShouldBe(code);
    }

    [Theory]
    [InlineData("hq")]       // lowercase
    [InlineData("-HQ")]      // leading hyphen
    [InlineData("H Q")]      // space
    [InlineData("H")]        // shorter than 2
    public void Invalid_branch_codes_are_rejected(string code)
    {
        Should.Throw<DomainException>(() => Branch.Create(Org, "Филиал", code, null, "Asia/Dushanbe", Now));
    }

    /// <summary>
    /// <c>API-031</c>: <c>Create</c> exposes no head-office parameter. Setting the flag at creation
    /// would open a path to «two head offices» before the unique index fires.
    /// </summary>
    [Fact]
    public void A_new_branch_is_never_a_head_office()
    {
        Branch.Create(Org, "Филиал Худжанд", "KHJ", null, "Asia/Dushanbe", Now).IsHeadOffice.ShouldBeFalse();
    }

    [Fact]
    public void Head_office_is_created_only_through_the_dedicated_factory()
    {
        Branch.CreateHeadOffice(Org, "Главный офис", "HQ", null, "Asia/Dushanbe", Now)
            .IsHeadOffice.ShouldBeTrue();
    }

    /// <summary><c>BRN-034</c>: an organization must never be left without a head office.</summary>
    [Fact]
    public void Head_office_cannot_be_deactivated_while_it_holds_the_flag()
    {
        var headOffice = Branch.CreateHeadOffice(Org, "Главный офис", "HQ", null, "Asia/Dushanbe", Now);

        Should.Throw<DomainException>(() => headOffice.Deactivate(Now))
            .Code.ShouldBe(DomainErrorCodes.HeadOfficeDeactivationForbidden);

        headOffice.ClearHeadOffice(Now);
        Should.NotThrow(() => headOffice.Deactivate(Now));
    }

    /// <summary><c>BRN-047</c>: only an active branch may become the head office.</summary>
    [Fact]
    public void An_inactive_branch_cannot_become_the_head_office()
    {
        var branch = Branch.Create(Org, "Филиал Худжанд", "KHJ", null, "Asia/Dushanbe", Now);
        branch.Deactivate(Now);

        Should.Throw<DomainException>(() => branch.MarkAsHeadOffice(Now))
            .Code.ShouldBe(DomainErrorCodes.HeadOfficeRequired);
    }

    /// <summary><c>BRN-053</c>: the head-office flag is unreachable from the edit path.</summary>
    [Fact]
    public void Updating_a_branch_preserves_its_head_office_flag()
    {
        var headOffice = Branch.CreateHeadOffice(Org, "Главный офис", "HQ", null, "Asia/Dushanbe", Now);

        headOffice.Update("Центральный офис", "HQ", "Душанбе, ул. Рудаки 1", "Asia/Dushanbe", Now);

        headOffice.IsHeadOffice.ShouldBeTrue();
        headOffice.Name.ShouldBe("Центральный офис");
        headOffice.NormalizedName.ShouldBe("ЦЕНТРАЛЬНЫЙ ОФИС");
    }
}
