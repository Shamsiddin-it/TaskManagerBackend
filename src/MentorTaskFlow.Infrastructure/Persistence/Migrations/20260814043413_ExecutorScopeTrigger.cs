using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MentorTaskFlow.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Re-expresses constraints 11 and 12 of TZ 12.2a so that transferring a user stays possible.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The contradiction.</b> The composite foreign keys added in the previous migration point at
    /// <c>users(id, organization_id, branch_id, category_id)</c>. A user's branch and category are
    /// exactly what <c>change-category</c> and <c>change-branch</c> alter, while
    /// <c>Assignment</c>'s scope is an immutable snapshot that is deliberately <b>never</b> re-pointed
    /// (<c>TEN-018</c>, <c>BRN-049</c>). With no <c>ON UPDATE</c> action the UPDATE of the users row is
    /// refused the moment any assignment references the old tuple — so the transfers of 15.2 and 39.6
    /// cannot run at all once the person has done a single piece of work.
    /// </para>
    /// <para>
    /// <b>Why not <c>ON UPDATE CASCADE</c>.</b> It would rewrite the branch and category of every
    /// historical assignment to follow the person. That silently moves finished work between branches,
    /// contradicting <c>TEN-018</c>, <c>BRN-049</c> and <c>ANA-014</c>, and would make a branch's
    /// analytics change retroactively whenever somebody transferred. It is the one outcome the
    /// immutable snapshot exists to prevent.
    /// </para>
    /// <para>
    /// <b>What replaces them.</b> The relation being guarded is a fact about <i>creation</i>: work may
    /// not be handed to a mentor of another branch or category, and the assigner may not be a Lead of
    /// another one (<c>TEST-TEN-014</c>, <c>TEST-TEN-015</c>). That is enforced here by a trigger on
    /// <c>assignments</c> which fires on INSERT and on any change of the two executor columns, and
    /// never when the users table changes. Existence and <c>ON DELETE RESTRICT</c> stay with ordinary
    /// foreign keys.
    /// </para>
    /// <para>
    /// The trigger raises SQLSTATE <c>23503</c> carrying the original constraint names, so a direct
    /// INSERT is still refused by the database under the name the specification gives it, and the
    /// application's existing translation to 409 <c>CROSS_SCOPE_REFERENCE</c> needs no change.
    /// </para>
    /// </remarks>
    public partial class ExecutorScopeTrigger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE assignments DROP CONSTRAINT fk_assignments_assignee_scope;
                ALTER TABLE assignments DROP CONSTRAINT fk_assignments_assigner_scope;
                """);

            // Existence and ON DELETE RESTRICT remain a foreign key's job: a user referenced by any
            // assignment can never be deleted, which is USER-022 and 11.7 unchanged.
            migrationBuilder.Sql("""
                ALTER TABLE assignments
                  ADD CONSTRAINT fk_assignments_assignee
                  FOREIGN KEY (assigned_to_id) REFERENCES users (id) ON DELETE RESTRICT;

                ALTER TABLE assignments
                  ADD CONSTRAINT fk_assignments_assigner
                  FOREIGN KEY (assigned_by_id) REFERENCES users (id) ON DELETE RESTRICT;
                """);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION assignments_assert_executor_scope() RETURNS trigger AS $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM users u
                        WHERE u.id = NEW.assigned_to_id
                          AND (u.organization_id <> NEW.organization_id
                            OR u.branch_id IS DISTINCT FROM NEW.branch_id
                            OR u.category_id IS DISTINCT FROM NEW.category_id))
                    THEN
                        RAISE EXCEPTION 'assignee is outside the scope of the assignment'
                            USING ERRCODE = '23503',
                                  CONSTRAINT = 'fk_assignments_assignee_scope',
                                  TABLE = 'assignments';
                    END IF;

                    IF NEW.assigned_by_id IS NOT NULL AND EXISTS (
                        SELECT 1 FROM users u
                        WHERE u.id = NEW.assigned_by_id
                          AND (u.organization_id <> NEW.organization_id
                            OR u.branch_id IS DISTINCT FROM NEW.branch_id
                            OR u.category_id IS DISTINCT FROM NEW.category_id))
                    THEN
                        RAISE EXCEPTION 'assigner is outside the scope of the assignment'
                            USING ERRCODE = '23503',
                                  CONSTRAINT = 'fk_assignments_assigner_scope',
                                  TABLE = 'assignments';
                    END IF;

                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                """);

            // OF assigned_to_id, assigned_by_id: the check belongs to the moment work is handed over.
            // Listing the columns is what keeps a later transfer of the person out of scope of the
            // trigger — the whole point of the change.
            migrationBuilder.Sql("""
                CREATE TRIGGER trg_assignments_executor_scope
                  BEFORE INSERT OR UPDATE OF assigned_to_id, assigned_by_id ON assignments
                  FOR EACH ROW EXECUTE FUNCTION assignments_assert_executor_scope();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_assignments_executor_scope ON assignments;
                DROP FUNCTION IF EXISTS assignments_assert_executor_scope();

                ALTER TABLE assignments DROP CONSTRAINT fk_assignments_assignee;
                ALTER TABLE assignments DROP CONSTRAINT fk_assignments_assigner;
                """);

            migrationBuilder.Sql("""
                ALTER TABLE assignments
                  ADD CONSTRAINT fk_assignments_assignee_scope
                  FOREIGN KEY (assigned_to_id, organization_id, branch_id, category_id)
                  REFERENCES users (id, organization_id, branch_id, category_id)
                  ON DELETE RESTRICT;

                ALTER TABLE assignments
                  ADD CONSTRAINT fk_assignments_assigner_scope
                  FOREIGN KEY (assigned_by_id, organization_id, branch_id, category_id)
                  REFERENCES users (id, organization_id, branch_id, category_id)
                  ON DELETE RESTRICT;
                """);
        }
    }
}
