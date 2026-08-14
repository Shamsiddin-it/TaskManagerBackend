using System.Reflection;
using MentorTaskFlow.Contracts.Submissions;
using MentorTaskFlow.Domain.Submissions;

namespace MentorTaskFlow.ArchitectureTests;

/// <summary>
/// The storage key stays on the server, and a submission never changes.
/// </summary>
/// <remarks>
/// <c>SUB-008</c> is the kind of rule that is broken by accident: someone adds a field to a DTO for
/// debugging and the key ships. A test that names the property is cheap and catches exactly that.
/// </remarks>
public sealed class SubmissionExposureTests
{
    [Theory]
    [InlineData(typeof(SubmissionDto))]
    [InlineData(typeof(FileUrlDto))]
    public void No_contract_carries_a_storage_key(Type contract)
    {
        var leaked = contract
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name.Contains("StorageKey", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name)
            .ToArray();

        leaked.ShouldBeEmpty(
            $"{contract.Name} must not expose a storage key: a file is reached through a presigned URL "
            + "issued after the permission checks are repeated (SUB-008).");
    }

    /// <summary>
    /// <c>SUB-020</c>: immutable. No role edits or deletes a submission, and a re-upload creates a new
    /// version instead — hence no public setter anywhere and no modification timestamp.
    /// </summary>
    [Fact]
    public void A_submission_cannot_be_modified()
    {
        typeof(Submission).GetProperty("UpdatedAt").ShouldBeNull();

        foreach (var property in typeof(Submission).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            (property.SetMethod?.IsPublic ?? false).ShouldBeFalse(
                $"Submission.{property.Name} must not be settable: a submission is never updated.");
        }
    }

    /// <summary>
    /// Append-only entities carry no concurrency token (11.6): a token would signal that somebody
    /// intends to update the row.
    /// </summary>
    [Fact]
    public void A_submission_has_no_concurrency_token() =>
        typeof(Submission).GetProperty("ConcurrencyToken").ShouldBeNull();

    [Fact]
    public void The_accepted_file_types_match_the_specification()
    {
        Enum.GetValues<FileExtension>().Length.ShouldBe(2);

        Submission.ContentTypeOf(FileExtension.Pdf).ShouldBe("application/pdf");
        Submission.ContentTypeOf(FileExtension.Pptx)
            .ShouldBe("application/vnd.openxmlformats-officedocument.presentationml.presentation");
    }
}
