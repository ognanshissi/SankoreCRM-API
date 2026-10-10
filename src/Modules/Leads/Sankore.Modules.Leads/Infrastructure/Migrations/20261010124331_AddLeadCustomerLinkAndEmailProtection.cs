using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLeadCustomerLinkAndEmailProtection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "customer_id",
                schema: "leads",
                table: "leads",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "customer_link_is_automatic",
                schema: "leads",
                table: "leads",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "customer_link_reason",
                schema: "leads",
                table: "leads",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "customer_linked_at",
                schema: "leads",
                table: "leads",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "email_blind_index",
                schema: "leads",
                table: "leads",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "email_enc",
                schema: "leads",
                table: "leads",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "product_line_id",
                schema: "leads",
                table: "leads",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_leads_tenant_id_customer_id",
                schema: "leads",
                table: "leads",
                columns: new[] { "tenant_id", "customer_id" },
                filter: "customer_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_leads_tenant_id_email_blind_index",
                schema: "leads",
                table: "leads",
                columns: new[] { "tenant_id", "email_blind_index" },
                filter: "email_blind_index IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_leads_tenant_id_customer_id",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropIndex(
                name: "ix_leads_tenant_id_email_blind_index",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "customer_id",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "customer_link_is_automatic",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "customer_link_reason",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "customer_linked_at",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "email_blind_index",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "email_enc",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "product_line_id",
                schema: "leads",
                table: "leads");
        }
    }
}
