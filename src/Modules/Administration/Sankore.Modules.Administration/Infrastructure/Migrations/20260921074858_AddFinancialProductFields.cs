using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Administration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFinancialProductFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "business_product_id",
                schema: "administration",
                table: "product_specialities",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "business_platform_name",
                schema: "administration",
                table: "product_specialities",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<string>(
                name: "category",
                schema: "administration",
                table: "product_specialities",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "created_at",
                schema: "administration",
                table: "product_specialities",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<DateOnly>(
                name: "effective_from",
                schema: "administration",
                table: "product_specialities",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "effective_to",
                schema: "administration",
                table: "product_specialities",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_active",
                schema: "administration",
                table: "product_specialities",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "parameters",
                schema: "administration",
                table: "product_specialities",
                type: "jsonb",
                nullable: true);

            // Backfill existing products: active by default, category = Loan
            migrationBuilder.Sql(
                """
                UPDATE administration.product_specialities SET is_active = true WHERE is_active = false;
                UPDATE administration.product_specialities SET category = 'Loan' WHERE category = '';
                UPDATE administration.product_specialities SET effective_from = CURRENT_DATE WHERE effective_from IS NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "ix_product_specialities_tenant_id_is_active",
                schema: "administration",
                table: "product_specialities",
                columns: new[] { "tenant_id", "is_active" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_product_specialities_tenant_id_is_active",
                schema: "administration",
                table: "product_specialities");

            migrationBuilder.DropColumn(
                name: "category",
                schema: "administration",
                table: "product_specialities");

            migrationBuilder.DropColumn(
                name: "created_at",
                schema: "administration",
                table: "product_specialities");

            migrationBuilder.DropColumn(
                name: "effective_from",
                schema: "administration",
                table: "product_specialities");

            migrationBuilder.DropColumn(
                name: "effective_to",
                schema: "administration",
                table: "product_specialities");

            migrationBuilder.DropColumn(
                name: "is_active",
                schema: "administration",
                table: "product_specialities");

            migrationBuilder.DropColumn(
                name: "parameters",
                schema: "administration",
                table: "product_specialities");

            migrationBuilder.AlterColumn<string>(
                name: "business_product_id",
                schema: "administration",
                table: "product_specialities",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "business_platform_name",
                schema: "administration",
                table: "product_specialities",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100);
        }
    }
}
