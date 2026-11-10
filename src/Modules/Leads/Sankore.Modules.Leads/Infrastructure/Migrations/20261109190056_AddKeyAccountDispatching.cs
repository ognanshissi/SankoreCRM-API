using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddKeyAccountDispatching : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "key_accounts_only",
                schema: "leads",
                table: "dispatching_rules",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "reserved_agent_ids",
                schema: "leads",
                table: "dispatching_rules",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "key_accounts_only",
                schema: "leads",
                table: "dispatching_rules");

            migrationBuilder.DropColumn(
                name: "reserved_agent_ids",
                schema: "leads",
                table: "dispatching_rules");
        }
    }
}
