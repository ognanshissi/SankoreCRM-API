using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <summary>
    /// Backfills columns that were added to the Lead entity over time but
    /// never captured in a migration Up() script. The EF model snapshot
    /// already includes them so no new migration add would detect the gap.
    /// </summary>
    public partial class BackfillMissingLeadColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "website",
                schema: "leads",
                table: "leads",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<string>(
                name: "campaign",
                schema: "leads",
                table: "leads",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "desired_amount",
                schema: "leads",
                table: "leads",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "captured_at",
                schema: "leads",
                table: "leads",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: DateTimeOffset.MinValue);

            migrationBuilder.AddColumn<string>(
                name: "channel",
                schema: "leads",
                table: "leads",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "comment",
                schema: "leads",
                table: "leads",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "converted_at",
                schema: "leads",
                table: "leads",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "converted_to_customer_id",
                schema: "leads",
                table: "leads",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "date_of_birth",
                schema: "leads",
                table: "leads",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "email",
                schema: "leads",
                table: "leads",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "external_reference",
                schema: "leads",
                table: "leads",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "first_name",
                schema: "leads",
                table: "leads",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "gender",
                schema: "leads",
                table: "leads",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<string>(
                name: "intent_level",
                schema: "leads",
                table: "leads",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_activity_at",
                schema: "leads",
                table: "leads",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_name",
                schema: "leads",
                table: "leads",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "owner_id",
                schema: "leads",
                table: "leads",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pipeline_stage",
                schema: "leads",
                table: "leads",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "New");

            migrationBuilder.AddColumn<double>(
                name: "qualification_completeness",
                schema: "leads",
                table: "leads",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                schema: "leads",
                table: "leads",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: DateTimeOffset.MinValue);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "campaign", schema: "leads", table: "leads");
            migrationBuilder.DropColumn(name: "captured_at", schema: "leads", table: "leads");
            migrationBuilder.DropColumn(name: "desired_amount", schema: "leads", table: "leads");
            migrationBuilder.DropColumn(name: "channel", schema: "leads", table: "leads");
            migrationBuilder.DropColumn(name: "comment", schema: "leads", table: "leads");
            migrationBuilder.DropColumn(name: "converted_at", schema: "leads", table: "leads");
            migrationBuilder.DropColumn(name: "converted_to_customer_id", schema: "leads", table: "leads");
            migrationBuilder.DropColumn(name: "date_of_birth", schema: "leads", table: "leads");
            migrationBuilder.DropColumn(name: "email", schema: "leads", table: "leads");
            migrationBuilder.DropColumn(name: "external_reference", schema: "leads", table: "leads");
            migrationBuilder.DropColumn(name: "first_name", schema: "leads", table: "leads");
            migrationBuilder.DropColumn(name: "gender", schema: "leads", table: "leads");
            migrationBuilder.DropColumn(name: "intent_level", schema: "leads", table: "leads");
            migrationBuilder.DropColumn(name: "last_activity_at", schema: "leads", table: "leads");
            migrationBuilder.DropColumn(name: "last_name", schema: "leads", table: "leads");
            migrationBuilder.DropColumn(name: "owner_id", schema: "leads", table: "leads");
            migrationBuilder.DropColumn(name: "pipeline_stage", schema: "leads", table: "leads");
            migrationBuilder.DropColumn(name: "qualification_completeness", schema: "leads", table: "leads");
            migrationBuilder.DropColumn(name: "updated_at", schema: "leads", table: "leads");
        }
    }
}
