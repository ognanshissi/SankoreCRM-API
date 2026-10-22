using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskCollaboration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "description",
                schema: "leads",
                table: "crm_tasks",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(1000)",
                oldMaxLength: 1000,
                oldNullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "created_by",
                schema: "leads",
                table: "crm_tasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "entity_id",
                schema: "leads",
                table: "crm_tasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "entity_type",
                schema: "leads",
                table: "crm_tasks",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            // Existing tasks on a lead get the generic pair too, so a task's subject is read from
            // one place whatever its age. lead_id stays: dispatching and the lead's tab read it.
            migrationBuilder.Sql(
                "UPDATE leads.crm_tasks SET entity_type = 'Lead', entity_id = lead_id WHERE lead_id IS NOT NULL;");

            migrationBuilder.CreateTable(
                name: "crm_task_attachments",
                schema: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    storage_ref = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    uploaded_by = table.Column<Guid>(type: "uuid", nullable: false),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_crm_task_attachments", x => x.id);
                    table.ForeignKey(
                        name: "fk_crm_task_attachments_crm_tasks_task_id",
                        column: x => x.task_id,
                        principalSchema: "leads",
                        principalTable: "crm_tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "crm_task_comments",
                schema: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_id = table.Column<Guid>(type: "uuid", nullable: false),
                    body = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    edited_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_crm_task_comments", x => x.id);
                    table.ForeignKey(
                        name: "fk_crm_task_comments_crm_tasks_task_id",
                        column: x => x.task_id,
                        principalSchema: "leads",
                        principalTable: "crm_tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "crm_task_criteria",
                schema: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    is_met = table.Column<bool>(type: "boolean", nullable: false),
                    met_by = table.Column<Guid>(type: "uuid", nullable: true),
                    met_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_crm_task_criteria", x => x.id);
                    table.ForeignKey(
                        name: "fk_crm_task_criteria_crm_tasks_task_id",
                        column: x => x.task_id,
                        principalSchema: "leads",
                        principalTable: "crm_tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "crm_task_participants",
                schema: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    added_by = table.Column<Guid>(type: "uuid", nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_crm_task_participants", x => x.id);
                    table.ForeignKey(
                        name: "fk_crm_task_participants_crm_tasks_task_id",
                        column: x => x.task_id,
                        principalSchema: "leads",
                        principalTable: "crm_tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "crm_task_tags",
                schema: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tag = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    added_by = table.Column<Guid>(type: "uuid", nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_crm_task_tags", x => x.id);
                    table.ForeignKey(
                        name: "fk_crm_task_tags_crm_tasks_task_id",
                        column: x => x.task_id,
                        principalSchema: "leads",
                        principalTable: "crm_tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_crm_tasks_tenant_id_entity_type_entity_id",
                schema: "leads",
                table: "crm_tasks",
                columns: new[] { "tenant_id", "entity_type", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "ix_crm_task_attachments_task_id_uploaded_at",
                schema: "leads",
                table: "crm_task_attachments",
                columns: new[] { "task_id", "uploaded_at" });

            migrationBuilder.CreateIndex(
                name: "ix_crm_task_comments_task_id_created_at",
                schema: "leads",
                table: "crm_task_comments",
                columns: new[] { "task_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_crm_task_criteria_task_id_position",
                schema: "leads",
                table: "crm_task_criteria",
                columns: new[] { "task_id", "position" });

            migrationBuilder.CreateIndex(
                name: "ix_crm_task_participants_task_id_user_id",
                schema: "leads",
                table: "crm_task_participants",
                columns: new[] { "task_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_crm_task_participants_tenant_id_user_id",
                schema: "leads",
                table: "crm_task_participants",
                columns: new[] { "tenant_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_crm_task_tags_task_id_tag",
                schema: "leads",
                table: "crm_task_tags",
                columns: new[] { "task_id", "tag" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_crm_task_tags_tenant_id_tag",
                schema: "leads",
                table: "crm_task_tags",
                columns: new[] { "tenant_id", "tag" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "crm_task_attachments",
                schema: "leads");

            migrationBuilder.DropTable(
                name: "crm_task_comments",
                schema: "leads");

            migrationBuilder.DropTable(
                name: "crm_task_criteria",
                schema: "leads");

            migrationBuilder.DropTable(
                name: "crm_task_participants",
                schema: "leads");

            migrationBuilder.DropTable(
                name: "crm_task_tags",
                schema: "leads");

            migrationBuilder.DropIndex(
                name: "ix_crm_tasks_tenant_id_entity_type_entity_id",
                schema: "leads",
                table: "crm_tasks");

            migrationBuilder.DropColumn(
                name: "created_by",
                schema: "leads",
                table: "crm_tasks");

            migrationBuilder.DropColumn(
                name: "entity_id",
                schema: "leads",
                table: "crm_tasks");

            migrationBuilder.DropColumn(
                name: "entity_type",
                schema: "leads",
                table: "crm_tasks");

            migrationBuilder.AlterColumn<string>(
                name: "description",
                schema: "leads",
                table: "crm_tasks",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(4000)",
                oldMaxLength: 4000,
                oldNullable: true);
        }
    }
}
