namespace MentorTaskFlow.Domain.Common;

/// <summary>
/// Produces the normalized forms that back the uniqueness indexes.
/// </summary>
/// <remarks>
/// Normalized values are computed by the server and can never be supplied by a client
/// (<c>ORG-020</c>). Centralising the rule matters because the value feeds unique indexes —
/// <c>ux_users_normalized_email</c>, <c>ux_categories_branch_normalized_name</c>,
/// <c>ux_branches_organization_normalized_name</c> — and two call sites normalising differently
/// would let a duplicate slip past the index.
/// </remarks>
public static class Normalization
{
    /// <summary>
    /// <c>ToUpperInvariant</c>, per TZ 10.1/10.2/10.17. Invariant culture is mandatory: under a
    /// Turkish locale a culture-sensitive uppercase maps «i» to «İ», so the same address would
    /// normalize differently depending on the host, and the uniqueness index would stop holding.
    /// </summary>
    public static string ToNormalized(string value) => value.Trim().ToUpperInvariant();
}
