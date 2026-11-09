using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWhatsAppTemplateHeaderFormat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "header_format",
                schema: "leads",
                table: "whatsapp_templates",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            // A header already synchronised with words is a TEXT one. A media header was dropped by
            // the sync before this column (its text null): it is recognised at the next hourly sync.
            migrationBuilder.Sql("UPDATE leads.whatsapp_templates SET header_format = 'TEXT' WHERE header_text IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "header_format",
                schema: "leads",
                table: "whatsapp_templates");
        }
    }
}
