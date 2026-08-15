using System.Data.Common;
using MentorTaskFlow.Infrastructure.Observability;
using MentorTaskFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace MentorTaskFlow.Infrastructure.Analytics;

/// <summary>One group's raw aggregates, before rounding and ratio arithmetic.</summary>
public sealed record MetricsRow
{
    public Guid? BranchId { get; init; }

    public Guid? CategoryId { get; init; }

    public Guid? MentorId { get; init; }

    public int TotalAssignments { get; init; }

    public int ApprovedAssignments { get; init; }

    public int FirstPassApproved { get; init; }

    public int OverdueAssignments { get; init; }

    public int SubmissionCount { get; init; }

    public int AssignmentsWithSubmissions { get; init; }

    public double? InitialSubmissionMedian { get; init; }

    public double? InitialSubmissionMean { get; init; }

    public int InitialSubmissionSamples { get; init; }

    public double? FirstReviewMedian { get; init; }

    public double? FirstReviewMean { get; init; }

    public int FirstReviewSamples { get; init; }

    public double? FinalReviewMedian { get; init; }

    public double? FinalReviewMean { get; init; }

    public int FinalReviewSamples { get; init; }

    public double? CycleMedian { get; init; }

    public double? CycleMean { get; init; }

    public int CycleSamples { get; init; }

    public int LateSubmissions { get; init; }

    public int PeriodSubmissions { get; init; }
}

/// <summary>
/// The SQL behind the ten metrics of 21.2.
/// </summary>
/// <remarks>
/// <para>
/// Written as SQL rather than LINQ for one reason that matters: <c>ANA-008</c> requires the median,
/// and <c>percentile_cont(0.5) WITHIN GROUP</c> has no LINQ translation. Once the medians are here,
/// keeping the rest alongside them means one pass over the data instead of ten.
/// </para>
/// <para>
/// Every metric attributes an assignment to the period by <b>one</b> named date (<c>ANA-003</c>), and
/// the dates differ per metric — which is why the counts are computed with filtered aggregates rather
/// than a single <c>WHERE</c>.
/// </para>
/// </remarks>
public sealed class MetricsQuery(MentorTaskFlowDbContext dbContext, AnalyticsMetrics metrics)
{
    public async Task<IReadOnlyList<MetricsRow>> ExecuteAsync(
        ReportScope scope,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtcExclusive,
        ReportFilters filters,
        CancellationToken cancellationToken)
    {
        var (selectColumns, groupBy) = GroupingSql(scope.Grouping);

        var sql = $"""
            WITH filtered AS (
                SELECT a.id,
                       a.branch_id,
                       a.category_id,
                       a.assigned_to_id,
                       a.assigned_at,
                       a.approved_at,
                       a.overdue_at,
                       a.first_submitted_at
                FROM assignments a
                WHERE a.organization_id = @organizationId
                  AND (@branchId IS NULL OR a.branch_id = @branchId)
                  AND (@categoryId IS NULL OR a.category_id = @categoryId)
                  AND (@mentorId IS NULL OR a.assigned_to_id = @mentorId)
                  AND (@source IS NULL OR a.source = @source)
                  AND a.status NOT IN ('Draft', 'Suggested')
                  AND (@includeCancelled OR a.status <> 'Cancelled')
                  AND (@templateType IS NULL OR EXISTS (
                        SELECT 1 FROM topic_assignments ta
                        WHERE ta.id = a.topic_assignment_id AND ta.type = @templateType))
            ),
            subs AS (
                SELECT s.assignment_id,
                       COUNT(*) AS versions,
                       MAX(s.submitted_at) AS last_submitted_at
                FROM submissions s
                JOIN filtered f ON f.id = s.assignment_id
                GROUP BY s.assignment_id
            ),
            first_review AS (
                SELECT r.assignment_id, MIN(r.created_at) AS first_review_at
                FROM reviews r
                JOIN filtered f ON f.id = r.assignment_id
                GROUP BY r.assignment_id
            ),
            approving_review AS (
                SELECT r.assignment_id, MAX(r.created_at) AS approved_review_at
                FROM reviews r
                JOIN filtered f ON f.id = r.assignment_id
                WHERE r.decision = 'Approved'
                GROUP BY r.assignment_id
            ),
            base AS (
                SELECT f.*,
                       COALESCE(s.versions, 0) AS versions,
                       s.last_submitted_at,
                       fr.first_review_at,
                       ar.approved_review_at
                FROM filtered f
                LEFT JOIN subs s ON s.assignment_id = f.id
                LEFT JOIN first_review fr ON fr.assignment_id = f.id
                LEFT JOIN approving_review ar ON ar.assignment_id = f.id
            ),
            durations AS (
                SELECT b.*,
                       CASE WHEN b.assigned_at IS NOT NULL AND b.first_submitted_at IS NOT NULL
                                 AND b.first_submitted_at >= b.assigned_at
                            THEN EXTRACT(EPOCH FROM (b.first_submitted_at - b.assigned_at)) / 3600 END
                            AS initial_hours,
                       CASE WHEN b.first_submitted_at IS NOT NULL AND b.first_review_at IS NOT NULL
                                 AND b.first_review_at >= b.first_submitted_at
                            THEN EXTRACT(EPOCH FROM (b.first_review_at - b.first_submitted_at)) / 3600 END
                            AS first_review_hours,
                       CASE WHEN b.last_submitted_at IS NOT NULL AND b.approved_review_at IS NOT NULL
                                 AND b.approved_review_at >= b.last_submitted_at
                            THEN EXTRACT(EPOCH FROM (b.approved_review_at - b.last_submitted_at)) / 3600 END
                            AS final_review_hours,
                       CASE WHEN b.assigned_at IS NOT NULL AND b.approved_at IS NOT NULL
                                 AND b.approved_at >= b.assigned_at
                            THEN EXTRACT(EPOCH FROM (b.approved_at - b.assigned_at)) / 3600 END
                            AS cycle_hours
                FROM base b
            ),
            period_submissions AS (
                SELECT s.assignment_id, f.branch_id, f.category_id, f.assigned_to_id, s.is_late
                FROM submissions s
                JOIN filtered f ON f.id = s.assignment_id
                WHERE s.submitted_at >= @fromUtc AND s.submitted_at < @toUtc
            )
            SELECT {selectColumns}
                   COUNT(*) FILTER (WHERE d.assigned_at >= @fromUtc AND d.assigned_at < @toUtc)
                       AS total_assignments,
                   COUNT(*) FILTER (WHERE d.approved_at >= @fromUtc AND d.approved_at < @toUtc)
                       AS approved_assignments,
                   COUNT(*) FILTER (WHERE d.approved_at >= @fromUtc AND d.approved_at < @toUtc
                                      AND d.versions = 1 AND d.approved_review_at IS NOT NULL)
                       AS first_pass_approved,
                   COUNT(*) FILTER (WHERE d.assigned_at >= @fromUtc AND d.assigned_at < @toUtc
                                      AND d.overdue_at IS NOT NULL)
                       AS overdue_assignments,
                   COALESCE(SUM(d.versions) FILTER (WHERE d.assigned_at >= @fromUtc AND d.assigned_at < @toUtc
                                                      AND d.versions > 0), 0)
                       AS submission_count,
                   COUNT(*) FILTER (WHERE d.assigned_at >= @fromUtc AND d.assigned_at < @toUtc
                                      AND d.versions > 0)
                       AS assignments_with_submissions,

                   percentile_cont(0.5) WITHIN GROUP (ORDER BY d.initial_hours)
                       FILTER (WHERE d.assigned_at >= @fromUtc AND d.assigned_at < @toUtc)
                       AS initial_submission_median,
                   AVG(d.initial_hours) FILTER (WHERE d.assigned_at >= @fromUtc AND d.assigned_at < @toUtc)
                       AS initial_submission_mean,
                   COUNT(d.initial_hours) FILTER (WHERE d.assigned_at >= @fromUtc AND d.assigned_at < @toUtc)
                       AS initial_submission_samples,

                   percentile_cont(0.5) WITHIN GROUP (ORDER BY d.first_review_hours)
                       FILTER (WHERE d.first_submitted_at >= @fromUtc AND d.first_submitted_at < @toUtc)
                       AS first_review_median,
                   AVG(d.first_review_hours) FILTER (WHERE d.first_submitted_at >= @fromUtc AND d.first_submitted_at < @toUtc)
                       AS first_review_mean,
                   COUNT(d.first_review_hours) FILTER (WHERE d.first_submitted_at >= @fromUtc AND d.first_submitted_at < @toUtc)
                       AS first_review_samples,

                   percentile_cont(0.5) WITHIN GROUP (ORDER BY d.final_review_hours)
                       FILTER (WHERE d.approved_at >= @fromUtc AND d.approved_at < @toUtc)
                       AS final_review_median,
                   AVG(d.final_review_hours) FILTER (WHERE d.approved_at >= @fromUtc AND d.approved_at < @toUtc)
                       AS final_review_mean,
                   COUNT(d.final_review_hours) FILTER (WHERE d.approved_at >= @fromUtc AND d.approved_at < @toUtc)
                       AS final_review_samples,

                   percentile_cont(0.5) WITHIN GROUP (ORDER BY d.cycle_hours)
                       FILTER (WHERE d.approved_at >= @fromUtc AND d.approved_at < @toUtc)
                       AS cycle_median,
                   AVG(d.cycle_hours) FILTER (WHERE d.approved_at >= @fromUtc AND d.approved_at < @toUtc)
                       AS cycle_mean,
                   COUNT(d.cycle_hours) FILTER (WHERE d.approved_at >= @fromUtc AND d.approved_at < @toUtc)
                       AS cycle_samples,

                   COUNT(*) FILTER (WHERE
                       (d.first_submitted_at IS NOT NULL AND d.assigned_at IS NOT NULL
                        AND d.first_submitted_at < d.assigned_at)
                    OR (d.first_review_at IS NOT NULL AND d.first_submitted_at IS NOT NULL
                        AND d.first_review_at < d.first_submitted_at)
                    OR (d.approved_at IS NOT NULL AND d.assigned_at IS NOT NULL
                        AND d.approved_at < d.assigned_at)) AS negative_durations,
                   (SELECT COUNT(*) FROM period_submissions ps
                     WHERE ps.is_late = true {GroupCorrelation(scope.Grouping, "ps", "d")}) AS late_submissions,
                   (SELECT COUNT(*) FROM period_submissions ps
                     WHERE true {GroupCorrelation(scope.Grouping, "ps", "d")}) AS period_submissions
            FROM durations d
            {groupBy}
            """;

        // The connection belongs to the DbContext, so it is opened and closed through EF and never
        // disposed here: `await using` on it would tear down the context's own connection and every
        // later query in the request would fail.
        var connection = dbContext.Database.GetDbConnection();
        var wasClosed = connection.State is System.Data.ConnectionState.Closed;

        if (wasClosed)
        {
            await dbContext.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;

            Add(command, "organizationId", scope.OrganizationId);
            Add(command, "branchId", scope.BranchId);
            Add(command, "categoryId", scope.CategoryId);
            Add(command, "mentorId", scope.MentorId);
            Add(command, "fromUtc", fromUtc);
            Add(command, "toUtc", toUtcExclusive);
            Add(command, "includeCancelled", filters.IncludeCancelled);
            Add(command, "source", filters.Source);
            Add(command, "templateType", filters.AssignmentType);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            var rows = new List<MetricsRow>();

            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(Read(reader, scope.Grouping));
            }

            return rows;
        }
        finally
        {
            if (wasClosed)
            {
                await dbContext.Database.CloseConnectionAsync();
            }
        }
    }

    /// <summary>
    /// The grouping columns, chosen from the closed set of <see cref="ReportGrouping"/>.
    /// </summary>
    /// <remarks>
    /// <c>TEN-071</c>: the category grain is always <c>(branch_id, category_id)</c>. Grouping by name
    /// would merge two unrelated study streams that happen to share a title, which the TZ classifies
    /// as a Critical defect — so the name is not among the options at all.
    /// </remarks>
    private static (string Select, string GroupBy) GroupingSql(ReportGrouping grouping) => grouping switch
    {
        ReportGrouping.BranchCategory =>
            ("d.branch_id, d.category_id, NULL::uuid AS assigned_to_id,", "GROUP BY d.branch_id, d.category_id"),

        ReportGrouping.Branch =>
            ("d.branch_id, NULL::uuid AS category_id, NULL::uuid AS assigned_to_id,", "GROUP BY d.branch_id"),

        ReportGrouping.Mentor =>
            ("d.branch_id, d.category_id, d.assigned_to_id,", "GROUP BY d.branch_id, d.category_id, d.assigned_to_id"),

        _ => ("NULL::uuid AS branch_id, NULL::uuid AS category_id, NULL::uuid AS assigned_to_id,", string.Empty),
    };

    /// <summary>Correlates the per-submission counts with the group of the outer row.</summary>
    private static string GroupCorrelation(ReportGrouping grouping, string inner, string outer) => grouping switch
    {
        ReportGrouping.BranchCategory =>
            $"AND {inner}.branch_id = {outer}.branch_id AND {inner}.category_id = {outer}.category_id",

        ReportGrouping.Branch => $"AND {inner}.branch_id = {outer}.branch_id",

        ReportGrouping.Mentor =>
            $"AND {inner}.branch_id = {outer}.branch_id AND {inner}.category_id = {outer}.category_id "
            + $"AND {inner}.assigned_to_id = {outer}.assigned_to_id",

        _ => string.Empty,
    };

    private MetricsRow Read(DbDataReader reader, ReportGrouping grouping)
    {
        // ANA-007: a negative duration is a data anomaly — a manual edit, a clock jump. It is excluded
        // from the aggregates by the CASE guards above and counted here, so the exclusion is visible
        // rather than silent.
        var negatives = (int)reader.GetInt64(reader.GetOrdinal("negative_durations"));

        var row = new MetricsRow
        {
            BranchId = GetNullableGuid(reader, "branch_id"),
            CategoryId = GetNullableGuid(reader, "category_id"),
            MentorId = grouping is ReportGrouping.Mentor ? GetNullableGuid(reader, "assigned_to_id") : null,
            TotalAssignments = (int)reader.GetInt64(reader.GetOrdinal("total_assignments")),
            ApprovedAssignments = (int)reader.GetInt64(reader.GetOrdinal("approved_assignments")),
            FirstPassApproved = (int)reader.GetInt64(reader.GetOrdinal("first_pass_approved")),
            OverdueAssignments = (int)reader.GetInt64(reader.GetOrdinal("overdue_assignments")),
            SubmissionCount = (int)reader.GetInt64(reader.GetOrdinal("submission_count")),
            AssignmentsWithSubmissions = (int)reader.GetInt64(reader.GetOrdinal("assignments_with_submissions")),
            InitialSubmissionMedian = GetNullableDouble(reader, "initial_submission_median"),
            InitialSubmissionMean = GetNullableDouble(reader, "initial_submission_mean"),
            InitialSubmissionSamples = (int)reader.GetInt64(reader.GetOrdinal("initial_submission_samples")),
            FirstReviewMedian = GetNullableDouble(reader, "first_review_median"),
            FirstReviewMean = GetNullableDouble(reader, "first_review_mean"),
            FirstReviewSamples = (int)reader.GetInt64(reader.GetOrdinal("first_review_samples")),
            FinalReviewMedian = GetNullableDouble(reader, "final_review_median"),
            FinalReviewMean = GetNullableDouble(reader, "final_review_mean"),
            FinalReviewSamples = (int)reader.GetInt64(reader.GetOrdinal("final_review_samples")),
            CycleMedian = GetNullableDouble(reader, "cycle_median"),
            CycleMean = GetNullableDouble(reader, "cycle_mean"),
            CycleSamples = (int)reader.GetInt64(reader.GetOrdinal("cycle_samples")),
            LateSubmissions = (int)reader.GetInt64(reader.GetOrdinal("late_submissions")),
            PeriodSubmissions = (int)reader.GetInt64(reader.GetOrdinal("period_submissions")),
        };

        if (negatives > 0)
        {
            metrics.NegativeDuration(negatives);
        }

        return row;
    }

    private static Guid? GetNullableGuid(DbDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);

        return reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);
    }

    private static double? GetNullableDouble(DbDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);

        return reader.IsDBNull(ordinal) ? null : Convert.ToDouble(reader.GetValue(ordinal));
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = new NpgsqlParameter(name, value ?? DBNull.Value);

        if (value is null)
        {
            parameter.NpgsqlDbType = name switch
            {
                "source" or "templateType" => NpgsqlDbType.Text,
                _ => NpgsqlDbType.Uuid,
            };
        }

        command.Parameters.Add(parameter);
    }
}

/// <summary>The filters of <c>ANA-010</c> that reach the query.</summary>
public sealed record ReportFilters(bool IncludeCancelled, string? Source, string? AssignmentType);
