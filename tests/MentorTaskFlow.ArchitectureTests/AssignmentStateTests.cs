using System.Reflection;
using MentorTaskFlow.Domain.Assignments;

namespace MentorTaskFlow.ArchitectureTests;

/// <summary>
/// <c>TEST-ASN-020</c>: the status moves only through domain methods.
/// </summary>
/// <remarks>
/// 10.6.5 requires <c>Status</c> to have a private setter so every transition passes the table of
/// 13.3. A public setter would let an application or infrastructure service assign a status directly,
/// skipping the guard, the terminal check and the event — and nothing would fail until somebody read
/// the history and found a gap.
/// </remarks>
public sealed class AssignmentStateTests
{
    [Fact]
    public void The_status_cannot_be_assigned_from_outside_the_domain()
    {
        var status = typeof(Assignment).GetProperty(nameof(Assignment.Status));

        status.ShouldNotBeNull();
        (status.SetMethod?.IsPublic ?? false).ShouldBeFalse(
            "Assignment.Status must not be publicly settable: every transition goes through a domain method (10.6.5).");
    }

    /// <summary>
    /// The same applies to every field a transition fills. A public setter on <c>ApprovedAt</c> or
    /// <c>OverdueAt</c> would let a caller fabricate a timeline the events do not support.
    /// </summary>
    [Theory]
    [InlineData(nameof(Assignment.Source))]
    [InlineData(nameof(Assignment.InitialDueAt))]
    [InlineData(nameof(Assignment.CurrentDueAt))]
    [InlineData(nameof(Assignment.AssignedAt))]
    [InlineData(nameof(Assignment.FirstSubmittedAt))]
    [InlineData(nameof(Assignment.ReviewStartedAt))]
    [InlineData(nameof(Assignment.ApprovedAt))]
    [InlineData(nameof(Assignment.OverdueAt))]
    [InlineData(nameof(Assignment.CancelledAt))]
    [InlineData(nameof(Assignment.CancelReason))]
    [InlineData(nameof(Assignment.LastEventSequence))]
    public void Lifecycle_fields_have_no_public_setter(string propertyName)
    {
        var property = typeof(Assignment).GetProperty(propertyName);

        property.ShouldNotBeNull();
        (property.SetMethod?.IsPublic ?? false).ShouldBeFalse(
            $"Assignment.{propertyName} is filled by a transition and must not be settable from outside.");
    }

    /// <summary>
    /// Scope is an immutable snapshot: an assignment stays a fact of the branch it was created in even
    /// after its mentor moves (10.6.4, <c>TEN-018</c>).
    /// </summary>
    [Theory]
    [InlineData(nameof(Assignment.OrganizationId))]
    [InlineData(nameof(Assignment.BranchId))]
    [InlineData(nameof(Assignment.CategoryId))]
    public void Scope_fields_have_no_public_setter(string propertyName)
    {
        typeof(Assignment).GetProperty(propertyName)!.SetMethod!.IsPublic.ShouldBeFalse();
    }

    /// <summary>
    /// Append-only, so no concurrency token and no modification timestamp: a token would signal that
    /// somebody intends to update the row (<c>EVT-001</c>).
    /// </summary>
    [Fact]
    public void Task_events_are_append_only()
    {
        typeof(TaskEvent).GetProperty("UpdatedAt").ShouldBeNull();

        foreach (var property in typeof(TaskEvent).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            (property.SetMethod?.IsPublic ?? false).ShouldBeFalse(
                $"TaskEvent.{property.Name} must not be settable: the journal is append-only.");
        }
    }

    /// <summary>
    /// Every status of 13.1 and every event type of Приложение F is present. A missing member would
    /// make a transition unrepresentable; an extra one would be a state the specification does not
    /// define.
    /// </summary>
    [Fact]
    public void The_enums_match_the_specification()
    {
        Enum.GetValues<AssignmentStatus>().Length.ShouldBe(9);
        Enum.GetValues<TaskEventType>().Length.ShouldBe(12);

        // Removed in 2.1: no Release 1.0 action produced it, and a deadline change is recorded by the
        // fields of ReviewNeedsRework instead.
        Enum.GetNames<TaskEventType>().ShouldNotContain("DueDateChanged");
    }
}
