using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Kyc.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddKycRemoteRouting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "requested_kinds",
                schema: "kyc",
                table: "kyc_remote_invitations",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "routed_at",
                schema: "kyc",
                table: "kyc_files",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "routing_reason",
                schema: "kyc",
                table: "kyc_files",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "validation_route",
                schema: "kyc",
                table: "kyc_files",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "requested_kinds",
                schema: "kyc",
                table: "kyc_remote_invitations");

            migrationBuilder.DropColumn(
                name: "routed_at",
                schema: "kyc",
                table: "kyc_files");

            migrationBuilder.DropColumn(
                name: "routing_reason",
                schema: "kyc",
                table: "kyc_files");

            migrationBuilder.DropColumn(
                name: "validation_route",
                schema: "kyc",
                table: "kyc_files");
        }
    }
}
