using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Kyc.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddKycFileAgency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "agency_id",
                schema: "kyc",
                table: "kyc_files",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_kyc_files_agency_status",
                schema: "kyc",
                table: "kyc_files",
                columns: new[] { "tenant_id", "agency_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_kyc_files_agency_status",
                schema: "kyc",
                table: "kyc_files");

            migrationBuilder.DropColumn(
                name: "agency_id",
                schema: "kyc",
                table: "kyc_files");
        }
    }
}
