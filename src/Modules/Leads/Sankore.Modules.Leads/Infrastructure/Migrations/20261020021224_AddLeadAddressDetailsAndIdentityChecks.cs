using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLeadAddressDetailsAndIdentityChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "identity_source_activity_id",
                schema: "leads",
                table: "leads",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "name_verified_at",
                schema: "leads",
                table: "leads",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "name_verified_by",
                schema: "leads",
                table: "leads",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "principal_landmark_enc",
                schema: "leads",
                table: "leads",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "principal_neighbourhood",
                schema: "leads",
                table: "leads",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "identity_source_activity_id",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "name_verified_at",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "name_verified_by",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "principal_landmark_enc",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "principal_neighbourhood",
                schema: "leads",
                table: "leads");
        }
    }
}
