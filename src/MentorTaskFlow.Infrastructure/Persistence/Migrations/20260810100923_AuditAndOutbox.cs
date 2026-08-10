using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MentorTaskFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuditAndOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_type = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    actor_role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    actor_admin_scope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    entity_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    http_method = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    path = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ip_address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    user_agent = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    result = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    failure_reason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    metadata = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    metadata_schema_version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_logs", x => x.id);
                    table.CheckConstraint("ck_audit_logs_actor_shape", "(actor_type = 'System' AND actor_id IS NULL) OR (actor_type = 'User' AND actor_id IS NOT NULL)");
                    table.CheckConstraint("ck_audit_logs_branch_scope", "branch_id IS NOT NULL OR action IN ('audit.read','bootstrap.provision','branch.activate','branch.create','branch.deactivate','branch.make_head_office','branch.update','organization.update','report.organization_export','security.scope_override_rejected','storage.cross_scope_inconsistency','user.change_admin_scope','user.change_branch','user.create_organization_admin')");
                    table.ForeignKey(
                        name: "fk_audit_logs_branch_scope",
                        columns: x => new { x.branch_id, x.organization_id },
                        principalTable: "branches",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_audit_logs_organization",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "notification_outbox",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    channel = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    event_type = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    payload = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    payload_schema_version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    status = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    last_attempt_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    last_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    deduplication_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    provider_message_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    is_system_alert = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    locked_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    locked_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_outbox", x => x.id);
                    table.CheckConstraint("ck_notification_outbox_attempts", "attempts BETWEEN 0 AND 5");
                    table.CheckConstraint("ck_notification_outbox_branch_scope", "branch_id IS NOT NULL OR event_type IN ('BranchWithoutAdmin','NotificationDeadLetter','OrganizationSystemAlert','UserInvitation')");
                    table.ForeignKey(
                        name: "fk_notification_outbox_organization",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_notification_outbox_user",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_outbox_branch_scope",
                        columns: x => new { x.branch_id, x.organization_id },
                        principalTable: "branches",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_branch_id_organization_id",
                table: "audit_logs",
                columns: new[] { "branch_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_organization_actor_occurred_at",
                table: "audit_logs",
                columns: new[] { "organization_id", "actor_id", "occurred_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_organization_branch_occurred_at",
                table: "audit_logs",
                columns: new[] { "organization_id", "branch_id", "occurred_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_organization_entity",
                table: "audit_logs",
                columns: new[] { "organization_id", "entity_type", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "ix_notification_outbox_branch_id_organization_id",
                table: "notification_outbox",
                columns: new[] { "branch_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_notification_outbox_organization_branch_status_created_at",
                table: "notification_outbox",
                columns: new[] { "organization_id", "branch_id", "status", "created_at" },
                descending: new[] { false, false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_notification_outbox_pending",
                table: "notification_outbox",
                columns: new[] { "status", "next_attempt_at" },
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ix_notification_outbox_processing",
                table: "notification_outbox",
                columns: new[] { "status", "locked_at" },
                filter: "status = 'Processing'");

            migrationBuilder.CreateIndex(
                name: "ix_notification_outbox_user_id",
                table: "notification_outbox",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_notification_outbox_dedup",
                table: "notification_outbox",
                column: "deduplication_key",
                unique: true);

            ApplyAuditLogGuards(migrationBuilder);
        }

        /// <summary>
        /// Makes <c>audit_logs</c> append-only at the database level (<c>AUD-001</c>, <c>DEPLOY-008</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// The application role loses UPDATE and DELETE outright, so a defect that tries to rewrite or
        /// erase an administrative record is refused by PostgreSQL even having reached the database.
        /// An audit trail the application can edit is not evidence.
        /// </para>
        /// <para>
        /// The retention role keeps DELETE — records expire after three years (<c>AUD-010</c>) — but is
        /// denied UPDATE, because retention nulls IP and user agent through a narrower path and must
        /// never be able to alter the action itself. <c>notification_outbox</c> stays fully mutable:
        /// rows legitimately change status, attempt count and lock as they are delivered.
        /// </para>
        /// <para>
        /// Guarded by a role-existence check: the CI Testcontainers instance has only the default
        /// superuser, and failing there would make the whole suite depend on roles that operations
        /// provisions, not the migration.
        /// </para>
        /// </remarks>
        private static void ApplyAuditLogGuards(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'mentortaskflow_app') THEN
                        REVOKE UPDATE, DELETE ON audit_logs FROM mentortaskflow_app;
                    END IF;

                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'mentortaskflow_retention') THEN
                        REVOKE UPDATE ON audit_logs FROM mentortaskflow_retention;
                    END IF;
                END
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_logs");

            migrationBuilder.DropTable(
                name: "notification_outbox");
        }
    }
}
