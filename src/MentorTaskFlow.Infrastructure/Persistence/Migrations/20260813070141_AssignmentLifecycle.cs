using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MentorTaskFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AssignmentLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "ak_topic_assignments_id_organization_id_branch_id_category_id",
                table: "topic_assignments",
                columns: new[] { "id", "organization_id", "branch_id", "category_id" });

            migrationBuilder.CreateTable(
                name: "assignments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    topic_assignment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assigned_to_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assigned_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    source = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    initial_due_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    current_due_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    generated_for_date = table.Column<DateOnly>(type: "date", nullable: true),
                    auto_generation_key = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    assigned_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    first_submitted_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    review_started_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    overdue_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    cancelled_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cancel_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    last_event_sequence = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assignments", x => x.id);
                    table.UniqueConstraint("ak_assignments_id_organization_id_branch_id_category_id", x => new { x.id, x.organization_id, x.branch_id, x.category_id });
                    table.CheckConstraint("ck_assignments_approved_fields", "(status <> 'Approved') OR (approved_at IS NOT NULL)");
                    table.CheckConstraint("ck_assignments_auto_fields", "(source = 'Auto' AND generated_for_date IS NOT NULL AND auto_generation_key IS NOT NULL)\nOR (source = 'Manual' AND generated_for_date IS NULL AND auto_generation_key IS NULL)");
                    table.CheckConstraint("ck_assignments_cancel_fields", "(status <> 'Cancelled')\nOR (cancelled_at IS NOT NULL AND cancelled_by_id IS NOT NULL\n    AND char_length(cancel_reason) BETWEEN 5 AND 500)");
                    table.CheckConstraint("ck_assignments_due_order", "current_due_at >= initial_due_at");
                    table.CheckConstraint("ck_assignments_status_allowed", "status IN ('Draft','Suggested','Assigned','Submitted','InReview','NeedsRework','Overdue','Approved','Cancelled')");
                    table.ForeignKey(
                        name: "fk_assignments_category_scope",
                        columns: x => new { x.category_id, x.organization_id, x.branch_id },
                        principalTable: "categories",
                        principalColumns: new[] { "id", "organization_id", "branch_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_assignments_template_scope",
                        columns: x => new { x.topic_assignment_id, x.organization_id, x.branch_id, x.category_id },
                        principalTable: "topic_assignments",
                        principalColumns: new[] { "id", "organization_id", "branch_id", "category_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "task_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    assignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence_number = table.Column<int>(type: "integer", nullable: false),
                    event_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    previous_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    new_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    submission_id = table.Column<Guid>(type: "uuid", nullable: true),
                    review_id = table.Column<Guid>(type: "uuid", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    metadata = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    metadata_schema_version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_task_events", x => x.id);
                    table.CheckConstraint("ck_task_events_system_actor", "(event_type NOT IN ('MarkedOverdue','SuggestedCreated')) OR actor_id IS NULL");
                    table.ForeignKey(
                        name: "fk_task_events_assignment_scope",
                        columns: x => new { x.assignment_id, x.organization_id, x.branch_id, x.category_id },
                        principalTable: "assignments",
                        principalColumns: new[] { "id", "organization_id", "branch_id", "category_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_assignments_approved_at",
                table: "assignments",
                column: "approved_at",
                filter: "approved_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_assignments_category_id_organization_id_branch_id",
                table: "assignments",
                columns: new[] { "category_id", "organization_id", "branch_id" });

            migrationBuilder.CreateIndex(
                name: "ix_assignments_overdue_scan",
                table: "assignments",
                columns: new[] { "status", "current_due_at" },
                filter: "status IN ('Assigned','NeedsRework')");

            migrationBuilder.CreateIndex(
                name: "ix_assignments_scope_assignee_status_due",
                table: "assignments",
                columns: new[] { "organization_id", "branch_id", "category_id", "assigned_to_id", "status", "current_due_at" });

            migrationBuilder.CreateIndex(
                name: "ix_assignments_suggestion_queue",
                table: "assignments",
                columns: new[] { "organization_id", "branch_id", "category_id", "status" },
                filter: "status = 'Suggested'");

            migrationBuilder.CreateIndex(
                name: "ix_assignments_topic_assignment_id_organization_id_branch_id_c",
                table: "assignments",
                columns: new[] { "topic_assignment_id", "organization_id", "branch_id", "category_id" });

            migrationBuilder.CreateIndex(
                name: "ux_assignments_auto_generation_key_scoped",
                table: "assignments",
                columns: new[] { "organization_id", "branch_id", "auto_generation_key" },
                unique: true,
                filter: "source = 'Auto'");

            migrationBuilder.CreateIndex(
                name: "ux_assignments_id_scope",
                table: "assignments",
                columns: new[] { "id", "organization_id", "branch_id", "category_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_task_events_assignment_id_organization_id_branch_id_categor",
                table: "task_events",
                columns: new[] { "assignment_id", "organization_id", "branch_id", "category_id" });

            migrationBuilder.CreateIndex(
                name: "ix_task_events_correlation_id",
                table: "task_events",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "ix_task_events_organization_branch_occurred_at",
                table: "task_events",
                columns: new[] { "organization_id", "branch_id", "occurred_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ux_task_events_assignment_sequence",
                table: "task_events",
                columns: new[] { "assignment_id", "sequence_number" },
                unique: true);

            AddExecutorScopeForeignKeys(migrationBuilder);
            ApplyTaskEventGuards(migrationBuilder);
        }

        /// <summary>
        /// Creates the two composite foreign keys tying an assignment's people to its scope
        /// (<c>TEN-024</c>, constraints 11 and 12 of TZ 12.2a).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Written as SQL rather than mapped through EF Core, and the reason matters. EF's
        /// <c>HasPrincipalKey</c> requires a real UNIQUE CONSTRAINT, which PostgreSQL permits only over
        /// NOT NULL columns — so the mapped version made EF try to alter <c>users.branch_id</c> and
        /// <c>users.category_id</c> to NOT NULL. That would destroy <c>USER-023</c>, under which an
        /// Organization Admin has both null by definition.
        /// </para>
        /// <para>
        /// PostgreSQL accepts a unique <b>index</b> as a foreign-key target, and
        /// <c>ux_users_id_scope</c> is exactly that, so the constraints are created directly and the
        /// nullability of the users table is left alone.
        /// </para>
        /// <para>
        /// What they buy: assigning work to a mentor of another branch or another category becomes
        /// physically impossible, as does recording a Lead of another category as the assigner. This is
        /// the mixing the whole tenancy model exists to prevent (<c>TEST-TEN-014</c>,
        /// <c>TEST-TEN-015</c>).
        /// </para>
        /// </remarks>
        private static void AddExecutorScopeForeignKeys(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE assignments
                  ADD CONSTRAINT fk_assignments_assignee_scope
                  FOREIGN KEY (assigned_to_id, organization_id, branch_id, category_id)
                  REFERENCES users (id, organization_id, branch_id, category_id)
                  ON DELETE RESTRICT;
                """);

            migrationBuilder.Sql("""
                ALTER TABLE assignments
                  ADD CONSTRAINT fk_assignments_assigner_scope
                  FOREIGN KEY (assigned_by_id, organization_id, branch_id, category_id)
                  REFERENCES users (id, organization_id, branch_id, category_id)
                  ON DELETE RESTRICT;
                """);
        }

        /// <summary>
        /// Makes <c>task_events</c> append-only at the database level (<c>EVT-001</c>, TZ 12.6).
        /// </summary>
        /// <remarks>
        /// The application role loses UPDATE and DELETE outright, so a defect that tries to rewrite the
        /// history of an assignment is refused by PostgreSQL even having reached the database. Events
        /// are kept indefinitely — they are the basis of the analytics and of any dispute — so the
        /// retention role gets nothing here either (TZ 27.5).
        /// </remarks>
        private static void ApplyTaskEventGuards(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'mentortaskflow_app') THEN
                        REVOKE UPDATE, DELETE ON task_events FROM mentortaskflow_app;
                    END IF;

                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'mentortaskflow_retention') THEN
                        REVOKE UPDATE, DELETE ON task_events FROM mentortaskflow_retention;
                    END IF;
                END
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "task_events");

            migrationBuilder.DropTable(
                name: "assignments");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_topic_assignments_id_organization_id_branch_id_category_id",
                table: "topic_assignments");
        }
    }
}
