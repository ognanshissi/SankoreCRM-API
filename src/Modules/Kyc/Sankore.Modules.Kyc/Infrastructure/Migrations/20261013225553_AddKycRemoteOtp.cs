using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Kyc.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddKycRemoteOtp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "otp_attempts",
                schema: "kyc",
                table: "kyc_remote_invitations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "otp_expires_at",
                schema: "kyc",
                table: "kyc_remote_invitations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "otp_hash",
                schema: "kyc",
                table: "kyc_remote_invitations",
                type: "bytea",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "otp_attempts",
                schema: "kyc",
                table: "kyc_remote_invitations");

            migrationBuilder.DropColumn(
                name: "otp_expires_at",
                schema: "kyc",
                table: "kyc_remote_invitations");

            migrationBuilder.DropColumn(
                name: "otp_hash",
                schema: "kyc",
                table: "kyc_remote_invitations");
        }
    }
}
