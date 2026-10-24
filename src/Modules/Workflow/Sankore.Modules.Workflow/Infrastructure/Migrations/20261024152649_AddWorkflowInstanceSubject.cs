using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Workflow.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowInstanceSubject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "subject_detail",
                schema: "workflow",
                table: "workflow_instances",
                type: "character varying(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "subject_label",
                schema: "workflow",
                table: "workflow_instances",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "subject_link",
                schema: "workflow",
                table: "workflow_instances",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "subject_detail",
                schema: "workflow",
                table: "workflow_instances");

            migrationBuilder.DropColumn(
                name: "subject_label",
                schema: "workflow",
                table: "workflow_instances");

            migrationBuilder.DropColumn(
                name: "subject_link",
                schema: "workflow",
                table: "workflow_instances");
        }
    }
}
