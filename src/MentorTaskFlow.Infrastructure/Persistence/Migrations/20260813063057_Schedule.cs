using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MentorTaskFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Schedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "topics",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day_number = table.Column<int>(type: "integer", nullable: false),
                    planned_date = table.Column<DateOnly>(type: "date", nullable: true),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_topics", x => x.id);
                    table.UniqueConstraint("ak_topics_id_organization_id_branch_id_category_id", x => new { x.id, x.organization_id, x.branch_id, x.category_id });
                    table.CheckConstraint("ck_topics_day_number", "day_number > 0");
                    table.ForeignKey(
                        name: "fk_topics_category_scope",
                        columns: x => new { x.category_id, x.organization_id, x.branch_id },
                        principalTable: "categories",
                        principalColumns: new[] { "id", "organization_id", "branch_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "topic_assignments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    topic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    is_required = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_topic_assignments", x => x.id);
                    table.CheckConstraint("ck_topic_assignments_type_allowed", "type IN ('Presentation','ClassTask','HomeTask')");
                    table.ForeignKey(
                        name: "fk_topic_assignments_topic_scope",
                        columns: x => new { x.topic_id, x.organization_id, x.branch_id, x.category_id },
                        principalTable: "topics",
                        principalColumns: new[] { "id", "organization_id", "branch_id", "category_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_topic_assignments_organization_branch_category_active",
                table: "topic_assignments",
                columns: new[] { "organization_id", "branch_id", "category_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ix_topic_assignments_topic_id_organization_id_branch_id_catego",
                table: "topic_assignments",
                columns: new[] { "topic_id", "organization_id", "branch_id", "category_id" });

            migrationBuilder.CreateIndex(
                name: "ux_topic_assignments_id_scope",
                table: "topic_assignments",
                columns: new[] { "id", "organization_id", "branch_id", "category_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_topics_category_id_organization_id_branch_id",
                table: "topics",
                columns: new[] { "category_id", "organization_id", "branch_id" });

            migrationBuilder.CreateIndex(
                name: "ix_topics_organization_branch_category_active_planned_date",
                table: "topics",
                columns: new[] { "organization_id", "branch_id", "category_id", "is_active", "planned_date" });

            migrationBuilder.CreateIndex(
                name: "ux_topics_category_day",
                table: "topics",
                columns: new[] { "category_id", "day_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_topics_category_planned_date",
                table: "topics",
                columns: new[] { "category_id", "planned_date" },
                unique: true,
                filter: "planned_date IS NOT NULL AND is_active = true");

            migrationBuilder.CreateIndex(
                name: "ux_topics_id_scope",
                table: "topics",
                columns: new[] { "id", "organization_id", "branch_id", "category_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "topic_assignments");

            migrationBuilder.DropTable(
                name: "topics");
        }
    }
}
