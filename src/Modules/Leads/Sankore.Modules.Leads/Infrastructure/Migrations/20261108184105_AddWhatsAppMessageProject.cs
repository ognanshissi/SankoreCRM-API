using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWhatsAppMessageProject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "opportunity_id",
                schema: "leads",
                table: "whatsapp_messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_whatsapp_messages_tenant_id_opportunity_id",
                schema: "leads",
                table: "whatsapp_messages",
                columns: new[] { "tenant_id", "opportunity_id" },
                filter: "opportunity_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_whatsapp_messages_tenant_id_opportunity_id",
                schema: "leads",
                table: "whatsapp_messages");

            migrationBuilder.DropColumn(
                name: "opportunity_id",
                schema: "leads",
                table: "whatsapp_messages");
        }
    }
}
