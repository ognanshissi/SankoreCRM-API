using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Admin.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UpdateTenantTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "RootUserEmail",
                table: "Tenants",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<string>(
                name: "SubscriptionState",
                table: "Tenants",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "subscription_state_info_next_renewal_at",
                table: "Tenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "subscription_state_info_renewal_period",
                table: "Tenants",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_RootUserEmail",
                table: "Tenants",
                column: "RootUserEmail",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_RootUserEmail_Fqdn",
                table: "Tenants",
                columns: new[] { "RootUserEmail", "Fqdn" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tenants_RootUserEmail",
                table: "Tenants");

            migrationBuilder.DropIndex(
                name: "IX_Tenants_RootUserEmail_Fqdn",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "SubscriptionState",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "subscription_state_info_next_renewal_at",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "subscription_state_info_renewal_period",
                table: "Tenants");

            migrationBuilder.AlterColumn<string>(
                name: "RootUserEmail",
                table: "Tenants",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100);
        }
    }
}
