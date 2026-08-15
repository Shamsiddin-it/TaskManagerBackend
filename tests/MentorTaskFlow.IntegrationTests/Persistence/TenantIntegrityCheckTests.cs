using MentorTaskFlow.Domain.Categories;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Domain.Users;
using Npgsql;

namespace MentorTaskFlow.IntegrationTests.Persistence;

/// <summary>
/// The restore-drill checks of <c>TEN-095</c>, run against a real database.
/// </summary>
/// <remarks>
/// <para>
/// <c>scripts/db/02-tenant-integrity.sql</c> is the deliverable, and a SQL file that nothing executes
/// rots quietly: a renamed column breaks it, and the breakage surfaces during a restore, which is the
/// worst moment to discover that the check meant to validate the restore does not run.
/// </para>
/// <para>
/// So the checks are executed here on the same schema the application uses. Two properties are
/// asserted — a healthy installation reports nothing, and a broken one reports the break. A check
/// that never fires is indistinguishable from a check that cannot fire.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class TenantIntegrityCheckTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Seeded = new(2026, 8, 15, 9, 0, 0, TimeSpan.Zero);

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        await SeedAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary><c>TEN-095</c>: a healthy installation reports nothing at all.</summary>
    [Fact]
    public async Task A_healthy_installation_passes_every_check()
    {
        foreach (var (index, statement) in Checks().Index())
        {
            var rows = await CountAsync(statement);

            rows.ShouldBe(0, $"check {index + 1} reported a violation on a healthy database.");
        }
    }

    /// <summary>
    /// <c>TEN-095.1</c>: an organization without a head office is a failed restore, not a warning.
    /// </summary>
    /// <remarks>
    /// This is what a partial restore of one tenant looks like from the database's side — the
    /// organization row arrived and its branches did not. Nothing in the schema forbids it, because
    /// «at least one branch» is not a constraint a row can carry; only a check like this finds it.
    /// </remarks>
    [Fact]
    public async Task An_organization_without_a_head_office_is_reported()
    {
        await using (var context = fixture.CreateContext(suppressTenantFilter: true))
        {
            context.Organizations.Add(Organization.Provision("Orphan Academy", "orphan-academy", Seeded));
            await context.SaveChangesAsync();
        }

        (await CountAsync(Checks()[0])).ShouldBe(1);
    }

    /// <summary><c>TEN-095.1</c>: two head offices in one organization is the same failure the other way.</summary>
    /// <remarks>
    /// The index is dropped and then put back in a <c>finally</c>. The Postgres container is a
    /// collection fixture shared by the whole run, so an index left dropped would silently disable
    /// <c>TEST-TEN-032</c> in whichever test happened to run next — a passing suite proving less than
    /// it claims, which is the worst failure a test can cause.
    /// </remarks>
    [Fact]
    public async Task A_second_head_office_is_reported()
    {
        await using var connection = await fixture.OpenRawConnectionAsync();

        try
        {
            // The unique index forbids this through the application, so the row is planted the way a
            // restore would produce it — data loaded while the constraint is absent, which is exactly
            // the window pg_restore leaves open between loading and recreating it.
            await ExecuteAsync(connection, "DROP INDEX ux_branches_single_head_office;");

            await ExecuteAsync(connection, """
                INSERT INTO branches (id, organization_id, name, normalized_name, code,
                                      address, time_zone_id, is_head_office, is_active, created_at, updated_at)
                SELECT gen_random_uuid(), organization_id, 'Второй головной', 'ВТОРОЙ ГОЛОВНОЙ',
                       'HQ2', NULL, time_zone_id, true, true, now(), now()
                  FROM branches
                 WHERE is_head_office
                 LIMIT 1;
                """);

            (await CountAsync(Checks()[0])).ShouldBe(1);
        }
        finally
        {
            // The planted row goes first: the index cannot be recreated while it violates it.
            await ExecuteAsync(connection, "DELETE FROM branches WHERE code = 'HQ2';");

            await ExecuteAsync(connection, """
                CREATE UNIQUE INDEX IF NOT EXISTS ux_branches_single_head_office
                    ON branches (organization_id)
                 WHERE is_head_office = true;
                """);
        }
    }

    // -----------------------------------------------------------------
    // Harness
    // -----------------------------------------------------------------

    /// <summary>
    /// The statements of the deliverable, read from the file itself.
    /// </summary>
    /// <remarks>
    /// Reading the file rather than restating the queries is the point: a copy here would keep
    /// passing after the file it is supposed to guard had stopped working. The <c>\echo</c> lines are
    /// psql meta-commands and are dropped; everything else is sent verbatim.
    /// </remarks>
    private static string[] Checks()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MentorTaskFlow.sln")))
        {
            directory = directory.Parent;
        }

        directory.ShouldNotBeNull("MentorTaskFlow.sln was not found above the test assembly.");

        var path = Path.Combine(directory!.FullName, "scripts", "db", "02-tenant-integrity.sql");
        File.Exists(path).ShouldBeTrue($"{path} is missing: TEN-095 has no check to run.");

        var sql = string.Join(
            '\n',
            File.ReadLines(path).Where(line =>
                !line.TrimStart().StartsWith("\\echo", StringComparison.Ordinal)
                && !line.TrimStart().StartsWith("--", StringComparison.Ordinal)));

        return [.. sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(statement => statement.Length > 0)];
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await command.ExecuteNonQueryAsync();
    }

    private async Task<int> CountAsync(string statement)
    {
        await using var connection = await fixture.OpenRawConnectionAsync();
        await using var command = connection.CreateCommand();

        command.CommandText = $"SELECT count(*) FROM ({statement}) AS violations;";

        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task SeedAsync()
    {
        await using var context = fixture.CreateContext(suppressTenantFilter: true);

        var organization = Organization.Provision("SoftClub Academy", "softclub-academy", Seeded);
        context.Organizations.Add(organization);

        var headOffice = Branch.CreateHeadOffice(organization.Id, "Главный офис", "HQ", null, "Asia/Dushanbe", Seeded);
        var khujand = Branch.Create(organization.Id, "Филиал Худжанд", "KHJ", null, "Asia/Dushanbe", Seeded);
        context.Branches.AddRange(headOffice, khujand);

        var sharp = Category.Create(organization.Id, headOffice.Id, "C#", null, Seeded);
        context.Categories.Add(sharp);
        context.CategorySettings.Add(CategorySettings.CreateDefault(sharp, "Asia/Dushanbe", Seeded));

        context.Users.Add(User.CreateMentor(
            organization.Id, headOffice.Id, sharp.Id, "Ментор", "mentor@mentortaskflow.test", Seeded));

        await context.SaveChangesAsync();
    }
}
