using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Kyc.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEncryptedBiometryPayloads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "encrypted_ocr_payload",
                schema: "kyc",
                table: "kyc_identity_documents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "encrypted_face_payload",
                schema: "kyc",
                table: "kyc_face_verifications",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "encrypted_ocr_payload",
                schema: "kyc",
                table: "kyc_identity_documents");

            migrationBuilder.DropColumn(
                name: "encrypted_face_payload",
                schema: "kyc",
                table: "kyc_face_verifications");
        }
    }
}
