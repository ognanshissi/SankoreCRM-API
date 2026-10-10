using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMultiPersonQualificationAndHealthConsent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_qualification_responses_lead_id",
                schema: "leads",
                table: "qualification_responses");

            migrationBuilder.RenameColumn(
                name: "answers_json",
                schema: "leads",
                table: "qualification_responses",
                newName: "answers");

            migrationBuilder.AddColumn<bool>(
                name: "exclude_from_scoring",
                schema: "leads",
                table: "qualification_templates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "is_health_questionnaire",
                schema: "leads",
                table: "qualification_templates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "subject_scope",
                schema: "leads",
                table: "qualification_templates",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                // "Lead" and not "": every template written before BNF-B-07 is answered once per
                // lead, and an empty string is not a value Enum.Parse accepts — the rows would
                // read back as a failure on the first template screen.
                defaultValue: "Lead");

            migrationBuilder.AlterColumn<Guid>(
                name: "answered_by",
                schema: "leads",
                table: "qualification_responses",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "answered_at",
                schema: "leads",
                table: "qualification_responses",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "created_at",
                schema: "leads",
                table: "qualification_responses",
                type: "timestamp with time zone",
                nullable: false,
                // now() rather than 0001-01-01: the generated default would put every existing
                // response at year 1, which every screen that orders by it then renders as the
                // oldest thing in the system. The Sql() below replaces it with answered_at, which
                // is what these rows' creation time actually was.
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<string>(
                name: "health_answers_enc",
                schema: "leads",
                table: "qualification_responses",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_health_data",
                schema: "leads",
                table: "qualification_responses",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "is_purged",
                schema: "leads",
                table: "qualification_responses",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "questionnaire_version",
                schema: "leads",
                table: "qualification_responses",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "respondent_id",
                schema: "leads",
                table: "qualification_responses",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status",
                schema: "leads",
                table: "qualification_responses",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                // "Completed": every row that exists was created by QualifyLeadHandler with its
                // answers. ToComplete is the status of the EXPECTED responses this evolution
                // introduces, and none of them exists yet.
                defaultValue: "Completed");

            migrationBuilder.AddColumn<Guid>(
                name: "subject_id",
                schema: "leads",
                table: "qualification_responses",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "subject_type",
                schema: "leads",
                table: "qualification_responses",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "Lead");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "transmitted_at",
                schema: "leads",
                table: "qualification_responses",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "subject_id",
                schema: "leads",
                table: "lead_consents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "subject_type",
                schema: "leads",
                table: "lead_consents",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_qualification_responses_tenant_id_transmitted_at",
                schema: "leads",
                table: "qualification_responses",
                columns: new[] { "tenant_id", "transmitted_at" },
                filter: "is_health_data AND NOT is_purged AND transmitted_at IS NOT NULL");

            // ── Backfill, before the unique index is built over these columns ────────────
            //
            // subject_id: new lead-scoped responses store the LEAD's id (see
            // QualificationResponse.Create), so existing rows are brought to the same convention
            // rather than left on Guid.Empty. Both satisfy the unique index, but two conventions
            // in one column is a trap for whoever reads it next.
            migrationBuilder.Sql(
                "UPDATE leads.qualification_responses SET subject_id = lead_id "
                + "WHERE subject_id = '00000000-0000-0000-0000-000000000000';");

            // created_at: these rows were created when they were answered.
            migrationBuilder.Sql(
                "UPDATE leads.qualification_responses SET created_at = answered_at "
                + "WHERE answered_at IS NOT NULL;");

            // ── Collapse the responses history, or the unique index cannot be built ──────
            //
            // QualifyLeadHandler used to Add a response unconditionally, so re-qualifying one lead
            // against one template left a row per attempt. Every such row now backfills to the
            // same (lead_id, template_id, 'Lead', lead_id) and the index below fails with 23505
            // — which is exactly what it did before this block existed.
            //
            // The newest answers win: answered_at, then created_at, then id, so the outcome does
            // not depend on physical row order. Keeping the latest is the new model's own rule —
            // one response per person per questionnaire, re-answered in place.
            //
            // This does NOT erase the qualification history. score_histories carries one row per
            // qualification with its score, trigger and factors, and only references the response
            // by an id with no foreign key; GetLeadTimelineHandler suppresses a score entry only
            // while its response still exists, so a collapsed attempt reappears on the timeline as
            // a score change instead of vanishing.
            //
            // Health responses are deliberately NOT collapsed: a health declaration carries legal
            // weight (BNF-B-09 keeps the row even when purging its content), so if one ever
            // duplicated, the index below must fail and a human must decide. No row can be health
            // data at this point anyway — is_health_data was added above, defaulting to false.
            migrationBuilder.Sql(
                """
                DELETE FROM leads.qualification_responses
                WHERE id IN (
                    SELECT id FROM (
                        SELECT id,
                               is_health_data,
                               row_number() OVER (
                                   PARTITION BY lead_id, template_id, subject_type, subject_id
                                   ORDER BY answered_at DESC NULLS LAST,
                                            created_at  DESC,
                                            id          DESC) AS rn
                        FROM leads.qualification_responses
                    ) ranked
                    WHERE ranked.rn > 1 AND NOT ranked.is_health_data
                );
                """);

            migrationBuilder.CreateIndex(
                name: "ux_qualification_responses_subject",
                schema: "leads",
                table: "qualification_responses",
                columns: new[] { "lead_id", "template_id", "subject_type", "subject_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_lead_consents_tenant_id_lead_id_type_subject_id",
                schema: "leads",
                table: "lead_consents",
                columns: new[] { "tenant_id", "lead_id", "type", "subject_id" },
                filter: "status = 'Active' AND subject_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_qualification_responses_tenant_id_transmitted_at",
                schema: "leads",
                table: "qualification_responses");

            migrationBuilder.DropIndex(
                name: "ux_qualification_responses_subject",
                schema: "leads",
                table: "qualification_responses");

            migrationBuilder.DropIndex(
                name: "ix_lead_consents_tenant_id_lead_id_type_subject_id",
                schema: "leads",
                table: "lead_consents");

            migrationBuilder.DropColumn(
                name: "exclude_from_scoring",
                schema: "leads",
                table: "qualification_templates");

            migrationBuilder.DropColumn(
                name: "is_health_questionnaire",
                schema: "leads",
                table: "qualification_templates");

            migrationBuilder.DropColumn(
                name: "subject_scope",
                schema: "leads",
                table: "qualification_templates");

            migrationBuilder.DropColumn(
                name: "created_at",
                schema: "leads",
                table: "qualification_responses");

            migrationBuilder.DropColumn(
                name: "health_answers_enc",
                schema: "leads",
                table: "qualification_responses");

            migrationBuilder.DropColumn(
                name: "is_health_data",
                schema: "leads",
                table: "qualification_responses");

            migrationBuilder.DropColumn(
                name: "is_purged",
                schema: "leads",
                table: "qualification_responses");

            migrationBuilder.DropColumn(
                name: "questionnaire_version",
                schema: "leads",
                table: "qualification_responses");

            migrationBuilder.DropColumn(
                name: "respondent_id",
                schema: "leads",
                table: "qualification_responses");

            migrationBuilder.DropColumn(
                name: "status",
                schema: "leads",
                table: "qualification_responses");

            migrationBuilder.DropColumn(
                name: "subject_id",
                schema: "leads",
                table: "qualification_responses");

            migrationBuilder.DropColumn(
                name: "subject_type",
                schema: "leads",
                table: "qualification_responses");

            migrationBuilder.DropColumn(
                name: "transmitted_at",
                schema: "leads",
                table: "qualification_responses");

            migrationBuilder.DropColumn(
                name: "subject_id",
                schema: "leads",
                table: "lead_consents");

            migrationBuilder.DropColumn(
                name: "subject_type",
                schema: "leads",
                table: "lead_consents");

            migrationBuilder.RenameColumn(
                name: "answers",
                schema: "leads",
                table: "qualification_responses",
                newName: "answers_json");

            migrationBuilder.AlterColumn<Guid>(
                name: "answered_by",
                schema: "leads",
                table: "qualification_responses",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "answered_at",
                schema: "leads",
                table: "qualification_responses",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)),
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_qualification_responses_lead_id",
                schema: "leads",
                table: "qualification_responses",
                column: "lead_id");
        }
    }
}
