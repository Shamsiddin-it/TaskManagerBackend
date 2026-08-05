using MentorTaskFlow.Application.Common.Exceptions;
using MentorTaskFlow.Application.Common.Security;

namespace MentorTaskFlow.UnitTests.Security;

/// <summary><c>AUTH-013</c>: 12–128 characters, a digit, an uppercase letter, not a common password.</summary>
public sealed class PasswordPolicyTests
{
    private static PasswordPolicy Policy(params string[] common) =>
        new(new StubCatalog(common));

    [Theory]
    [InlineData("Karimov2026Task")]
    [InlineData("Xy1aaaaaaaaaaa")]
    public void A_conforming_password_is_accepted(string password)
    {
        Policy().Evaluate(password).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Short1")]
    [InlineData("Abcdefgh123")]
    public void A_password_below_twelve_characters_is_rejected(string password)
    {
        Policy().Evaluate(password).ShouldContain(e => e.Contains("от 12 до 128"));
    }

    [Fact]
    public void A_password_above_the_maximum_is_rejected()
    {
        var password = "A1" + new string('a', PasswordPolicy.MaxLength);

        Policy().Evaluate(password).ShouldContain(e => e.Contains("от 12 до 128"));
    }

    [Fact]
    public void A_password_without_a_digit_is_rejected()
    {
        Policy().Evaluate("Karimovtaskflow").ShouldContain(e => e.Contains("цифру"));
    }

    [Fact]
    public void A_password_without_an_uppercase_letter_is_rejected()
    {
        Policy().Evaluate("karimov2026task").ShouldContain(e => e.Contains("заглавную"));
    }

    [Fact]
    public void A_common_password_is_rejected_even_when_it_satisfies_every_other_rule()
    {
        var policy = Policy("Password1234");

        // The point of the list: this value passes length, digit and uppercase, and is still among the
        // first guesses any attacker makes.
        policy.Evaluate("Password1234").ShouldContain(e => e.Contains("распространённых"));
    }

    [Fact]
    public void The_common_password_check_ignores_case()
    {
        Policy("Password1234").Evaluate("PASSWORD1234").ShouldContain(e => e.Contains("распространённых"));
    }

    /// <summary>
    /// Every violated rule is reported at once. Revealing them one per attempt would turn setting a
    /// password into a guessing game.
    /// </summary>
    [Fact]
    public void All_violations_are_reported_together()
    {
        var errors = Policy().Evaluate("short").ToArray();

        errors.Length.ShouldBe(3);
    }

    [Fact]
    public void Validate_throws_a_validation_exception_carrying_every_message()
    {
        var exception = Should.Throw<ValidationAppException>(() => Policy().Validate("short"));

        exception.Errors.ShouldContainKey("newPassword");
        exception.Errors["newPassword"].Length.ShouldBe(3);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_missing_password_is_rejected_without_further_rules(string? password)
    {
        Policy().Evaluate(password).ShouldHaveSingleItem();
    }

    private sealed class StubCatalog(string[] passwords) : ICommonPasswordCatalog
    {
        public bool Contains(string password) =>
            passwords.Contains(password, StringComparer.OrdinalIgnoreCase);
    }
}
