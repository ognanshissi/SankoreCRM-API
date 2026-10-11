using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Customers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddClientAmlProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "activity_sector_code",
                schema: "customers",
                table: "clients",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "activity_sector_detail",
                schema: "customers",
                table: "clients",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "corporate_purpose",
                schema: "customers",
                table: "clients",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "encrypted_funds_origin_detail",
                schema: "customers",
                table: "clients",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "funds_origin_code",
                schema: "customers",
                table: "clients",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "registration_extract_issued_on",
                schema: "customers",
                table: "clients",
                type: "date",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_clients_registration_extract",
                schema: "customers",
                table: "clients",
                columns: new[] { "tenant_id", "registration_extract_issued_on" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_clients_registration_extract",
                schema: "customers",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "activity_sector_code",
                schema: "customers",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "activity_sector_detail",
                schema: "customers",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "corporate_purpose",
                schema: "customers",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "encrypted_funds_origin_detail",
                schema: "customers",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "funds_origin_code",
                schema: "customers",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "registration_extract_issued_on",
                schema: "customers",
                table: "clients");
        }
    }
}
