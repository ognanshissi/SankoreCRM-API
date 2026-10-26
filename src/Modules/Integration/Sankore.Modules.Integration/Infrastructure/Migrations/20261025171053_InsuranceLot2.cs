using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Integration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InsuranceLot2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_ins_subscription_holding",
                schema: "integration",
                table: "ins_subscription");

            migrationBuilder.DropIndex(
                name: "ux_ins_subscription_idempotency",
                schema: "integration",
                table: "ins_subscription");

            migrationBuilder.AddColumn<string>(
                name: "collection_mandate_reference",
                schema: "integration",
                table: "integration_connection",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cover_note_mandate_reference",
                schema: "integration",
                table: "integration_connection",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "has_collection_mandate",
                schema: "integration",
                table: "integration_connection",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "has_cover_note_mandate",
                schema: "integration",
                table: "integration_connection",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "commitment_reference",
                schema: "integration",
                table: "ins_subscription",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "commitment_signed_on",
                schema: "integration",
                table: "ins_subscription",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "information_sheet_delivery_id",
                schema: "integration",
                table: "ins_subscription",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "payment_due_by",
                schema: "integration",
                table: "ins_subscription",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "premium_paid_at",
                schema: "integration",
                table: "ins_subscription",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "current_information_sheet_id",
                schema: "integration",
                table: "ins_product",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "current_information_sheet_version",
                schema: "integration",
                table: "ins_product",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "exclusions",
                schema: "integration",
                table: "ins_product",
                type: "character varying(8000)",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ins_information_sheet",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    file_reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    uploaded_by = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ins_information_sheet", x => x.id);
                    table.ForeignKey(
                        name: "fk_ins_information_sheet_ins_product_product_id",
                        column: x => x.product_id,
                        principalSchema: "integration",
                        principalTable: "ins_product",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ins_information_sheet_delivery",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    holding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    crm_customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sheet_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sheet_version = table.Column<int>(type: "integer", nullable: false),
                    sheet_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    channel = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    acknowledgement_reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    delivered_by = table.Column<Guid>(type: "uuid", nullable: false),
                    retain_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ins_information_sheet_delivery", x => x.id);
                    table.ForeignKey(
                        name: "fk_ins_information_sheet_delivery_information_sheets_sheet_id",
                        column: x => x.sheet_id,
                        principalSchema: "integration",
                        principalTable: "ins_information_sheet",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_ins_subscription_holding",
                schema: "integration",
                table: "ins_subscription",
                columns: new[] { "tenant_id", "crm_holding_id", "clause_version" },
                unique: true,
                filter: "crm_holding_id IS NOT NULL AND status NOT IN ('DebitFailed', 'Reversed', 'Cancelled', 'PremiumRefundDue', 'Refused')");

            migrationBuilder.CreateIndex(
                name: "ux_ins_subscription_idempotency",
                schema: "integration",
                table: "ins_subscription",
                columns: new[] { "tenant_id", "crm_customer_id", "product_id", "effective_date" },
                unique: true,
                filter: "status NOT IN ('DebitFailed', 'Reversed', 'Cancelled', 'PremiumRefundDue', 'Refused')");

            migrationBuilder.CreateIndex(
                name: "ix_ins_information_sheet_product_id",
                schema: "integration",
                table: "ins_information_sheet",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ux_ins_information_sheet_version",
                schema: "integration",
                table: "ins_information_sheet",
                columns: new[] { "tenant_id", "product_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ins_information_sheet_delivery_holding",
                schema: "integration",
                table: "ins_information_sheet_delivery",
                columns: new[] { "tenant_id", "holding_id" });

            migrationBuilder.CreateIndex(
                name: "ix_ins_information_sheet_delivery_sheet_id",
                schema: "integration",
                table: "ins_information_sheet_delivery",
                column: "sheet_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ins_information_sheet_delivery",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "ins_information_sheet",
                schema: "integration");

            migrationBuilder.DropIndex(
                name: "ux_ins_subscription_holding",
                schema: "integration",
                table: "ins_subscription");

            migrationBuilder.DropIndex(
                name: "ux_ins_subscription_idempotency",
                schema: "integration",
                table: "ins_subscription");

            migrationBuilder.DropColumn(
                name: "collection_mandate_reference",
                schema: "integration",
                table: "integration_connection");

            migrationBuilder.DropColumn(
                name: "cover_note_mandate_reference",
                schema: "integration",
                table: "integration_connection");

            migrationBuilder.DropColumn(
                name: "has_collection_mandate",
                schema: "integration",
                table: "integration_connection");

            migrationBuilder.DropColumn(
                name: "has_cover_note_mandate",
                schema: "integration",
                table: "integration_connection");

            migrationBuilder.DropColumn(
                name: "commitment_reference",
                schema: "integration",
                table: "ins_subscription");

            migrationBuilder.DropColumn(
                name: "commitment_signed_on",
                schema: "integration",
                table: "ins_subscription");

            migrationBuilder.DropColumn(
                name: "information_sheet_delivery_id",
                schema: "integration",
                table: "ins_subscription");

            migrationBuilder.DropColumn(
                name: "payment_due_by",
                schema: "integration",
                table: "ins_subscription");

            migrationBuilder.DropColumn(
                name: "premium_paid_at",
                schema: "integration",
                table: "ins_subscription");

            migrationBuilder.DropColumn(
                name: "current_information_sheet_id",
                schema: "integration",
                table: "ins_product");

            migrationBuilder.DropColumn(
                name: "current_information_sheet_version",
                schema: "integration",
                table: "ins_product");

            migrationBuilder.DropColumn(
                name: "exclusions",
                schema: "integration",
                table: "ins_product");

            migrationBuilder.CreateIndex(
                name: "ux_ins_subscription_holding",
                schema: "integration",
                table: "ins_subscription",
                columns: new[] { "tenant_id", "crm_holding_id", "clause_version" },
                unique: true,
                filter: "crm_holding_id IS NOT NULL AND status NOT IN ('DebitFailed', 'Reversed', 'Cancelled', 'PremiumRefundDue')");

            migrationBuilder.CreateIndex(
                name: "ux_ins_subscription_idempotency",
                schema: "integration",
                table: "ins_subscription",
                columns: new[] { "tenant_id", "crm_customer_id", "product_id", "effective_date" },
                unique: true,
                filter: "status NOT IN ('DebitFailed', 'Reversed', 'Cancelled', 'PremiumRefundDue')");
        }
    }
}
