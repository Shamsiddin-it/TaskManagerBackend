using MentorTaskFlow.Application.Common.Exceptions;
using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Domain.Common;

namespace MentorTaskFlow.UnitTests.Common;

public sealed class AppExceptionTests
{
    [Fact]
    public void NotFound_always_uses_the_single_isolation_code()
    {
        new NotFoundException().Code.ShouldBe(ErrorCodes.ResourceNotFound);
    }

    [Fact]
    public void Forbidden_defaults_to_FORBIDDEN_but_accepts_a_narrower_403_code()
    {
        new ForbiddenException().Code.ShouldBe(ErrorCodes.Forbidden);
        new ForbiddenException(ErrorCodes.ScopeOverrideForbidden).Code.ShouldBe(ErrorCodes.ScopeOverrideForbidden);
    }

    [Fact]
    public void Validation_exposes_field_errors_for_the_problem_details_errors_object()
    {
        var exception = new ValidationAppException("pageSize", "Значение вне диапазона 1–100.");

        exception.Code.ShouldBe(ErrorCodes.ValidationFailed);
        exception.Errors.ShouldContainKey("pageSize");
        exception.Errors["pageSize"].ShouldHaveSingleItem();
    }

    /// <summary>
    /// The Domain project references nothing, so it cannot see <see cref="ErrorCodes"/> at compile
    /// time. This test is the compensating check that a domain code is still a catalog code.
    /// </summary>
    [Fact]
    public void Domain_exception_codes_belong_to_the_catalog()
    {
        var exception = new DomainException(
            ErrorCodes.AssignmentInvalidStatusTransition,
            "Переход не определён в таблице 13.3.");

        ErrorCodes.StatusByCode.ShouldContainKey(exception.Code);
        ErrorCodes.StatusByCode[exception.Code].ShouldBe(409);
    }

    [Fact]
    public void Conflict_carries_structured_details_for_the_client()
    {
        var exception = new ConflictException(
            ErrorCodes.BranchChangeBlocked,
            details: new Dictionary<string, object?> { ["blockingAssignmentIds"] = new[] { Guid.Empty } });

        exception.Details.ShouldContainKey("blockingAssignmentIds");
    }
}
