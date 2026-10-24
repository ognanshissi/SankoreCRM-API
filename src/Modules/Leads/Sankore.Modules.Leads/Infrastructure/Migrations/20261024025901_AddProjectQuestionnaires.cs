using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectQuestionnaires : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_qualification_responses_subject",
                schema: "leads",
                table: "qualification_responses");

            migrationBuilder.AlterColumn<Guid>(
                name: "lead_id",
                schema: "leads",
                table: "qualification_responses",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "opportunity_id",
                schema: "leads",
                table: "qualification_responses",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "requires_questionnaires",
                schema: "leads",
                table: "project_template_steps",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "ux_qualification_responses_project_subject",
                schema: "leads",
                table: "qualification_responses",
                columns: new[] { "opportunity_id", "template_id", "subject_type", "subject_id" },
                unique: true,
                filter: "opportunity_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_qualification_responses_subject",
                schema: "leads",
                table: "qualification_responses",
                columns: new[] { "lead_id", "template_id", "subject_type", "subject_id" },
                unique: true,
                filter: "opportunity_id IS NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_qualification_responses_opportunities_opportunity_id",
                schema: "leads",
                table: "qualification_responses",
                column: "opportunity_id",
                principalSchema: "leads",
                principalTable: "opportunities",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_qualification_responses_opportunities_opportunity_id",
                schema: "leads",
                table: "qualification_responses");

            migrationBuilder.DropIndex(
                name: "ux_qualification_responses_project_subject",
                schema: "leads",
                table: "qualification_responses");

            migrationBuilder.DropIndex(
                name: "ux_qualification_responses_subject",
                schema: "leads",
                table: "qualification_responses");

            migrationBuilder.DropColumn(
                name: "opportunity_id",
                schema: "leads",
                table: "qualification_responses");

            migrationBuilder.DropColumn(
                name: "requires_questionnaires",
                schema: "leads",
                table: "project_template_steps");

            migrationBuilder.AlterColumn<Guid>(
                name: "lead_id",
                schema: "leads",
                table: "qualification_responses",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_qualification_responses_subject",
                schema: "leads",
                table: "qualification_responses",
                columns: new[] { "lead_id", "template_id", "subject_type", "subject_id" },
                unique: true);
        }
    }
}
