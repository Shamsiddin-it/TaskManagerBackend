using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MentorTaskFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Submissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "submissions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    assignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    storage_key = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    original_file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    content_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    file_extension = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    file_size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256_hash = table.Column<string>(type: "char(64)", nullable: false),
                    is_late = table.Column<bool>(type: "boolean", nullable: false),
                    submitted_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    preview_storage_key = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    conversion_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_submissions", x => x.id);
                    table.CheckConstraint("ck_submissions_conversion_status", "conversion_status IS NULL");
                    table.CheckConstraint("ck_submissions_extension_allowed", "file_extension IN ('Pdf','Pptx')");
                    table.CheckConstraint("ck_submissions_preview_key", "(file_extension = 'Pdf' AND preview_storage_key = storage_key) OR (file_extension = 'Pptx' AND preview_storage_key IS NULL)");
                    table.CheckConstraint("ck_submissions_sha256_format", "sha256_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_submissions_size_bounds", "file_size_bytes > 0 AND file_size_bytes <= 52428800");
                    table.CheckConstraint("ck_submissions_version_positive", "version_number >= 1");
                    table.ForeignKey(
                        name: "fk_submissions_assignment_scope",
                        columns: x => new { x.assignment_id, x.organization_id, x.branch_id, x.category_id },
                        principalTable: "assignments",
                        principalColumns: new[] { "id", "organization_id", "branch_id", "category_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_submissions_assignment_scope",
                table: "submissions",
                columns: new[] { "assignment_id", "organization_id", "branch_id", "category_id" });

            migrationBuilder.CreateIndex(
                name: "ix_submissions_scope_submitted_at",
                table: "submissions",
                columns: new[] { "organization_id", "branch_id", "category_id", "submitted_at" },
                descending: new[] { false, false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_submissions_sha256_hash",
                table: "submissions",
                column: "sha256_hash");

            migrationBuilder.CreateIndex(
                name: "ux_submissions_assignment_version",
                table: "submissions",
                columns: new[] { "assignment_id", "version_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_submissions_id_scope",
                table: "submissions",
                columns: new[] { "id", "organization_id", "branch_id", "category_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "submissions");
        }
    }
}
