using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MentorTaskFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Reviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_submissions_id_scope",
                table: "submissions");

            migrationBuilder.AddUniqueConstraint(
                name: "ak_submissions_id_organization_id_branch_id_category_id",
                table: "submissions",
                columns: new[] { "id", "organization_id", "branch_id", "category_id" });

            migrationBuilder.CreateTable(
                name: "reviews",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    submission_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reviewer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    decision = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    comment = table.Column<string>(type: "character varying(3000)", maxLength: 3000, nullable: true),
                    rework_due_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reviews", x => x.id);
                    table.CheckConstraint("ck_reviews_decision_allowed", "decision IN ('Approved','NeedsRework')");
                    table.CheckConstraint("ck_reviews_decision_fields", "(decision = 'NeedsRework' AND char_length(comment) BETWEEN 10 AND 3000 AND rework_due_at IS NOT NULL) OR (decision = 'Approved' AND rework_due_at IS NULL)");
                    table.ForeignKey(
                        name: "fk_reviews_submission_scope",
                        columns: x => new { x.submission_id, x.organization_id, x.branch_id, x.category_id },
                        principalTable: "submissions",
                        principalColumns: new[] { "id", "organization_id", "branch_id", "category_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_reviews_assignment_id",
                table: "reviews",
                column: "assignment_id");

            migrationBuilder.CreateIndex(
                name: "ix_reviews_scope_created_at",
                table: "reviews",
                columns: new[] { "organization_id", "branch_id", "category_id", "created_at" },
                descending: new[] { false, false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_reviews_submission_scope",
                table: "reviews",
                columns: new[] { "submission_id", "organization_id", "branch_id", "category_id" });

            migrationBuilder.CreateIndex(
                name: "ux_reviews_submission",
                table: "reviews",
                column: "submission_id",
                unique: true);

            AddReviewerScopeGuard(migrationBuilder);
        }

        /// <summary>
        /// Constraint 15 of TZ 12.2a, expressed the way Phase 9 had to express 11 and 12.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The specification writes it as a composite foreign key onto
        /// <c>users(id, organization_id, branch_id, category_id)</c>. Those are the columns a category
        /// or branch transfer changes, and a review is an immutable record that is never re-pointed —
        /// so the foreign key would refuse to let any Lead who has ever reviewed anything be
        /// transferred, and <c>ON UPDATE CASCADE</c> would instead rewrite the branch of their past
        /// decisions to follow them. The same reasoning, and the same resolution, as
        /// <c>fk_assignments_assignee_scope</c>.
        /// </para>
        /// <para>
        /// Existence and <c>ON DELETE RESTRICT</c> stay an ordinary foreign key. The scope half becomes
        /// a trigger that fires on INSERT and on a change of <c>reviewer_id</c>, and never when the
        /// users table moves. It raises SQLSTATE <c>23503</c> under the specification's own constraint
        /// name, so a direct INSERT is still refused by the database under that name.
        /// </para>
        /// </remarks>
        private static void AddReviewerScopeGuard(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE reviews
                  ADD CONSTRAINT fk_reviews_reviewer
                  FOREIGN KEY (reviewer_id) REFERENCES users (id) ON DELETE RESTRICT;
                """);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION reviews_assert_reviewer_scope() RETURNS trigger AS $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM users u
                        WHERE u.id = NEW.reviewer_id
                          AND (u.organization_id <> NEW.organization_id
                            OR u.branch_id IS DISTINCT FROM NEW.branch_id
                            OR u.category_id IS DISTINCT FROM NEW.category_id))
                    THEN
                        RAISE EXCEPTION 'reviewer is outside the scope of the submission'
                            USING ERRCODE = '23503',
                                  CONSTRAINT = 'fk_reviews_reviewer_scope',
                                  TABLE = 'reviews';
                    END IF;

                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER trg_reviews_reviewer_scope
                  BEFORE INSERT OR UPDATE OF reviewer_id ON reviews
                  FOR EACH ROW EXECUTE FUNCTION reviews_assert_reviewer_scope();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_reviews_reviewer_scope ON reviews;
                DROP FUNCTION IF EXISTS reviews_assert_reviewer_scope();
                """);

            migrationBuilder.DropTable(
                name: "reviews");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_submissions_id_organization_id_branch_id_category_id",
                table: "submissions");

            migrationBuilder.CreateIndex(
                name: "ux_submissions_id_scope",
                table: "submissions",
                columns: new[] { "id", "organization_id", "branch_id", "category_id" },
                unique: true);
        }
    }
}
