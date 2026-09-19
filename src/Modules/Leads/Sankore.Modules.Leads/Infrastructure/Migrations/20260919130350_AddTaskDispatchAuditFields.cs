using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskDispatchAuditFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "compatibility_factors_json",
                schema: "leads",
                table: "crm_tasks",
                type: "jsonb",
                nullable: true,
                defaultValueSql: "NULL");

            migrationBuilder.AddColumn<double>(
                name: "compatibility_score",
                schema: "leads",
                table: "crm_tasks",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "compatibility_factors_json",
                schema: "leads",
                table: "crm_tasks");

            migrationBuilder.DropColumn(
                name: "compatibility_score",
                schema: "leads",
                table: "crm_tasks");
        }
    }
}
