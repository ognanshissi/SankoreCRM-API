using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Administration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddKycRemotePolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "kyc_remote_policy",
                schema: "administration",
                table: "product_specialities",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");

            migrationBuilder.AddColumn<bool>(
                name: "kyc_auto_approval_enabled",
                schema: "administration",
                table: "compliance_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "kyc_auto_approval_max_tier",
                schema: "administration",
                table: "compliance_settings",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Simplified");

            migrationBuilder.AddColumn<int>(
                name: "kyc_auto_approval_sample_pct",
                schema: "administration",
                table: "compliance_settings",
                type: "integer",
                nullable: false,
                defaultValue: 10);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "kyc_remote_policy",
                schema: "administration",
                table: "product_specialities");

            migrationBuilder.DropColumn(
                name: "kyc_auto_approval_enabled",
                schema: "administration",
                table: "compliance_settings");

            migrationBuilder.DropColumn(
                name: "kyc_auto_approval_max_tier",
                schema: "administration",
                table: "compliance_settings");

            migrationBuilder.DropColumn(
                name: "kyc_auto_approval_sample_pct",
                schema: "administration",
                table: "compliance_settings");
        }
    }
}
