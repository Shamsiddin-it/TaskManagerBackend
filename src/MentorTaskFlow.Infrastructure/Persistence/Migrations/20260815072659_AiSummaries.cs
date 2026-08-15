using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MentorTaskFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AiSummaries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_logs_branch_scope",
                table: "audit_logs");

            migrationBuilder.CreateTable(
                name: "ai_summaries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    scope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    subject_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    period_start = table.Column<DateOnly>(type: "date", nullable: false),
                    period_end = table.Column<DateOnly>(type: "date", nullable: false),
                    cache_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    metrics_hash = table.Column<string>(type: "char(64)", nullable: false),
                    prompt_version = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    model_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    content = table.Column<string>(type: "text", nullable: true),
                    failure_reason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    input_tokens = table.Column<int>(type: "integer", nullable: true),
                    output_tokens = table.Column<int>(type: "integer", nullable: true),
                    requested_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_forced_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_summaries", x => x.id);
                    table.CheckConstraint("ck_ai_summaries_completed_content", "status <> 'Completed' OR content IS NOT NULL");
                    table.CheckConstraint("ck_ai_summaries_personal_subject", "scope <> 'Personal' OR subject_user_id IS NOT NULL");
                    table.CheckConstraint("ck_ai_summaries_scope_shape", "(scope = 'Organization' AND branch_id IS NULL AND category_id IS NULL) OR (scope = 'Branch' AND branch_id IS NOT NULL AND category_id IS NULL) OR (scope IN ('Personal', 'Team') AND branch_id IS NOT NULL AND category_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_ai_summaries_branch_scope",
                        columns: x => new { x.branch_id, x.organization_id },
                        principalTable: "branches",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ai_summaries_category",
                        column: x => x.category_id,
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ai_summaries_organization",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ai_summaries_requested_by",
                        column: x => x.requested_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ai_summaries_subject_user",
                        column: x => x.subject_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_logs_branch_scope",
                table: "audit_logs",
                sql: "branch_id IS NOT NULL OR action IN ('ai.summary_generate','audit.read','bootstrap.provision','branch.activate','branch.create','branch.deactivate','branch.make_head_office','branch.update','organization.update','report.organization_export','security.scope_override_rejected','storage.cross_scope_inconsistency','user.change_admin_scope','user.change_branch','user.create_organization_admin')");

            migrationBuilder.CreateIndex(
                name: "ix_ai_summaries_branch_id_organization_id",
                table: "ai_summaries",
                columns: new[] { "branch_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_summaries_category_id",
                table: "ai_summaries",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_summaries_created_at",
                table: "ai_summaries",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_ai_summaries_forced",
                table: "ai_summaries",
                columns: new[] { "organization_id", "scope", "subject_user_id", "last_forced_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_summaries_organization_branch_category_period",
                table: "ai_summaries",
                columns: new[] { "organization_id", "branch_id", "category_id", "period_start", "period_end" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_summaries_requested_by_id",
                table: "ai_summaries",
                column: "requested_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_summaries_subject_user_id",
                table: "ai_summaries",
                column: "subject_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_ai_summaries_cache_key",
                table: "ai_summaries",
                column: "cache_key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_summaries");

            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_logs_branch_scope",
                table: "audit_logs");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_logs_branch_scope",
                table: "audit_logs",
                sql: "branch_id IS NOT NULL OR action IN ('audit.read','bootstrap.provision','branch.activate','branch.create','branch.deactivate','branch.make_head_office','branch.update','organization.update','report.organization_export','security.scope_override_rejected','storage.cross_scope_inconsistency','user.change_admin_scope','user.change_branch','user.create_organization_admin')");
        }
    }
}
