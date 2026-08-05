using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MentorTaskFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialTenancy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "organizations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    normalized_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    slug = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_organizations", x => x.id);
                    table.CheckConstraint("ck_organizations_slug_format", "slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$' AND char_length(slug) BETWEEN 2 AND 80");
                });

            migrationBuilder.CreateTable(
                name: "branches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    normalized_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    time_zone_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    is_head_office = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_branches", x => x.id);
                    table.UniqueConstraint("ak_branches_id_organization_id", x => new { x.id, x.organization_id });
                    table.CheckConstraint("ck_branches_code_format", "code ~ '^[A-Z0-9][A-Z0-9-]*$' AND char_length(code) BETWEEN 2 AND 32");
                    table.CheckConstraint("ck_branches_name_length", "char_length(name) BETWEEN 2 AND 200");
                    table.ForeignKey(
                        name: "fk_branches_organization",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "categories",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    normalized_name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_categories", x => x.id);
                    table.UniqueConstraint("ak_categories_id_organization_id_branch_id", x => new { x.id, x.organization_id, x.branch_id });
                    table.ForeignKey(
                        name: "fk_categories_branch_scope",
                        columns: x => new { x.branch_id, x.organization_id },
                        principalTable: "branches",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "category_settings",
                columns: table => new
                {
                    category_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    time_zone_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    default_assignment_due_days = table.Column<int>(type: "integer", nullable: false),
                    default_due_time_local = table.Column<TimeOnly>(type: "time", nullable: false),
                    deadline_reminder_hours = table.Column<int>(type: "integer", nullable: false),
                    allow_late_submission = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_category_settings", x => x.category_id);
                    table.CheckConstraint("ck_category_settings_due_days", "default_assignment_due_days BETWEEN 1 AND 60");
                    table.CheckConstraint("ck_category_settings_reminder_hours", "deadline_reminder_hours BETWEEN 1 AND 168");
                    table.ForeignKey(
                        name: "fk_category_settings_scope",
                        columns: x => new { x.category_id, x.organization_id, x.branch_id },
                        principalTable: "categories",
                        principalColumns: new[] { "id", "organization_id", "branch_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    full_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    normalized_email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    password_hash = table.Column<string>(type: "text", nullable: true),
                    role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    admin_scope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    telegram_chat_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    token_version = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    failed_login_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    lockout_until = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    last_login_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                    table.CheckConstraint("ck_users_admin_scope_allowed", "admin_scope IS NULL OR admin_scope IN ('Organization','Branch')");
                    table.CheckConstraint("ck_users_role_admin_scope", "(role = 'Admin' AND admin_scope IS NOT NULL) OR (role <> 'Admin' AND admin_scope IS NULL)");
                    table.CheckConstraint("ck_users_role_allowed", "role IN ('Admin','Lead','Mentor')");
                    table.CheckConstraint("ck_users_role_category", "(role = 'Admin' AND category_id IS NULL) OR (role IN ('Lead','Mentor') AND category_id IS NOT NULL)");
                    table.CheckConstraint("ck_users_scope_shape", "(role = 'Admin' AND admin_scope = 'Organization' AND branch_id IS NULL AND category_id IS NULL)\nOR (role = 'Admin' AND admin_scope = 'Branch' AND branch_id IS NOT NULL AND category_id IS NULL)\nOR (role IN ('Lead','Mentor') AND branch_id IS NOT NULL AND category_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_users_branch_scope",
                        columns: x => new { x.branch_id, x.organization_id },
                        principalTable: "branches",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_users_category_scope",
                        columns: x => new { x.category_id, x.organization_id, x.branch_id },
                        principalTable: "categories",
                        principalColumns: new[] { "id", "organization_id", "branch_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_users_organization",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "user_branch_history",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    old_branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    new_branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    old_category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    new_category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    changed_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_branch_history", x => x.id);
                    table.CheckConstraint("ck_user_branch_history_change", "old_branch_id IS DISTINCT FROM new_branch_id");
                    table.CheckConstraint("ck_user_branch_history_reason", "char_length(reason) BETWEEN 5 AND 500");
                    table.ForeignKey(
                        name: "fk_user_branch_history_new_branch_scope",
                        columns: x => new { x.new_branch_id, x.organization_id },
                        principalTable: "branches",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_user_branch_history_old_branch_scope",
                        columns: x => new { x.old_branch_id, x.organization_id },
                        principalTable: "branches",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_user_branch_history_user",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "user_category_history",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    previous_category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    new_category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    previous_role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    new_role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    changed_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_category_history", x => x.id);
                    table.CheckConstraint("ck_user_category_history_reason", "char_length(reason) BETWEEN 5 AND 500");
                    table.ForeignKey(
                        name: "fk_user_category_history_scope",
                        columns: x => new { x.branch_id, x.organization_id },
                        principalTable: "branches",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_user_category_history_user",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_branches_organization_is_active",
                table: "branches",
                columns: new[] { "organization_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ux_branches_id_organization",
                table: "branches",
                columns: new[] { "id", "organization_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_branches_organization_code",
                table: "branches",
                columns: new[] { "organization_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_branches_organization_normalized_name",
                table: "branches",
                columns: new[] { "organization_id", "normalized_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_branches_single_head_office",
                table: "branches",
                column: "organization_id",
                unique: true,
                filter: "is_head_office = true");

            migrationBuilder.CreateIndex(
                name: "ix_categories_branch_id_organization_id",
                table: "categories",
                columns: new[] { "branch_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_categories_organization_branch_is_active",
                table: "categories",
                columns: new[] { "organization_id", "branch_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ux_categories_branch_normalized_name",
                table: "categories",
                columns: new[] { "branch_id", "normalized_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_categories_id_scope",
                table: "categories",
                columns: new[] { "id", "organization_id", "branch_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_category_settings_category_id_organization_id_branch_id",
                table: "category_settings",
                columns: new[] { "category_id", "organization_id", "branch_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_organizations_normalized_name",
                table: "organizations",
                column: "normalized_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_organizations_slug",
                table: "organizations",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_branch_history_new_branch_id_organization_id",
                table: "user_branch_history",
                columns: new[] { "new_branch_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_user_branch_history_old_branch_id_organization_id",
                table: "user_branch_history",
                columns: new[] { "old_branch_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_user_branch_history_organization_user_changed_at",
                table: "user_branch_history",
                columns: new[] { "organization_id", "user_id", "changed_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_user_branch_history_user_id",
                table: "user_branch_history",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_category_history_branch_id_organization_id",
                table: "user_category_history",
                columns: new[] { "branch_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_user_category_history_organization_user_changed_at",
                table: "user_category_history",
                columns: new[] { "organization_id", "user_id", "changed_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_user_category_history_user_id",
                table: "user_category_history",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_users_branch_id_organization_id",
                table: "users",
                columns: new[] { "branch_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_users_category_id_organization_id_branch_id",
                table: "users",
                columns: new[] { "category_id", "organization_id", "branch_id" });

            migrationBuilder.CreateIndex(
                name: "ix_users_organization_branch_category_role_is_active",
                table: "users",
                columns: new[] { "organization_id", "branch_id", "category_id", "role", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ix_users_organization_branch_role_is_active",
                table: "users",
                columns: new[] { "organization_id", "branch_id", "role", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ix_users_organization_role_admin_scope_is_active",
                table: "users",
                columns: new[] { "organization_id", "role", "admin_scope", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ux_users_active_lead_per_category",
                table: "users",
                column: "category_id",
                unique: true,
                filter: "role = 'Lead' AND is_active = true");

            migrationBuilder.CreateIndex(
                name: "ux_users_id_scope",
                table: "users",
                columns: new[] { "id", "organization_id", "branch_id", "category_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_users_normalized_email",
                table: "users",
                column: "normalized_email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_users_telegram_chat_id",
                table: "users",
                column: "telegram_chat_id",
                unique: true,
                filter: "telegram_chat_id IS NOT NULL");

            ApplyAppendOnlyGuards(migrationBuilder);
        }

        /// <summary>
        /// Revokes UPDATE and DELETE on the append-only tables from the application role (TZ 12.6,
        /// <c>DEPLOY-008</c>, <c>USER-026</c>, <c>BRN-025</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// «Append-only» becomes a database guarantee rather than a coding rule: a defect that tries
        /// to rewrite history is refused by PostgreSQL even if it reaches the database. Migrations run
        /// as <c>mentortaskflow_migrator</c>, a different role with full rights, so the revoke does
        /// not block schema evolution.
        /// </para>
        /// <para>
        /// The statements are guarded by a role-existence check: the CI Testcontainers instance has
        /// only the default superuser, and a hard failure there would make the whole suite depend on
        /// a role that is provisioned by operations, not by the migration.
        /// </para>
        /// </remarks>
        private static void ApplyAppendOnlyGuards(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'mentortaskflow_app') THEN
                        REVOKE UPDATE, DELETE ON user_category_history, user_branch_history
                          FROM mentortaskflow_app;
                    END IF;

                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'mentortaskflow_retention') THEN
                        -- Retention receives DELETE only on tables with a bounded lifetime (TZ 27.5).
                        -- The history tables are not among them: transfer history is kept for the
                        -- lifetime of the organization.
                        REVOKE UPDATE, DELETE ON user_category_history, user_branch_history
                          FROM mentortaskflow_retention;
                    END IF;
                END
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "category_settings");

            migrationBuilder.DropTable(
                name: "user_branch_history");

            migrationBuilder.DropTable(
                name: "user_category_history");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "categories");

            migrationBuilder.DropTable(
                name: "branches");

            migrationBuilder.DropTable(
                name: "organizations");
        }
    }
}
