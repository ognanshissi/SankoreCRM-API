using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMultiSourceLeadImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "file_reference",
                schema: "leads",
                table: "lead_import_jobs",
                newName: "source_reference");

            migrationBuilder.AlterColumn<string>(
                name: "original_file_name",
                schema: "leads",
                table: "lead_import_jobs",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<string>(
                name: "defaults_json",
                schema: "leads",
                table: "lead_import_jobs",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_type",
                schema: "leads",
                table: "lead_import_jobs",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                // Every row that predates multi-source import was a file upload.
                defaultValue: "File");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "defaults_json",
                schema: "leads",
                table: "lead_import_jobs");

            migrationBuilder.DropColumn(
                name: "source_type",
                schema: "leads",
                table: "lead_import_jobs");

            migrationBuilder.RenameColumn(
                name: "source_reference",
                schema: "leads",
                table: "lead_import_jobs",
                newName: "file_reference");

            migrationBuilder.AlterColumn<string>(
                name: "original_file_name",
                schema: "leads",
                table: "lead_import_jobs",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }
    }
}
