using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Kyc.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOcrFieldConfidences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ocr_field_confidences_json",
                schema: "kyc",
                table: "kyc_identity_documents",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ocr_field_confidences_json",
                schema: "kyc",
                table: "kyc_identity_documents");
        }
    }
}
