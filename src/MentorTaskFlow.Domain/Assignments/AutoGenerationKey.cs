namespace MentorTaskFlow.Domain.Assignments;

/// <summary>
/// The idempotency key of an auto-generated suggestion (<c>SCH-009</c>).
/// </summary>
/// <remarks>
/// <para>
/// The template is
/// <c>{OrganizationId:N}:{BranchId:N}:{CategoryId:N}:{TopicAssignmentId:N}:{GeneratedForDate:yyyy-MM-dd}:{Source}</c>.
/// </para>
/// <para>
/// It deliberately does <b>not</b> include the mentor. A second run must not create a second task
/// merely because the balancing picked someone else — the identity of the executor is an outcome of
/// the run, not part of what makes the suggestion unique.
/// </para>
/// <para>
/// The scope segments are redundant in the strict sense: <c>TopicAssignmentId</c> is already globally
/// unique. They are included anyway (<c>SCH-023</c>) so the key stays correct if identifier generation
/// ever changes, so the index gains tenant-leading columns and can serve branch-scoped plans, and so
/// the key can be read by a person during an incident without a database at hand.
/// </para>
/// </remarks>
public static class AutoGenerationKey
{
    public static string For(
        Guid organizationId,
        Guid branchId,
        Guid categoryId,
        Guid topicAssignmentId,
        DateOnly generatedForDate) =>
        $"{organizationId:N}:{branchId:N}:{categoryId:N}:{topicAssignmentId:N}:"
        + $"{generatedForDate:yyyy-MM-dd}:{AssignmentSource.Auto}";
}
