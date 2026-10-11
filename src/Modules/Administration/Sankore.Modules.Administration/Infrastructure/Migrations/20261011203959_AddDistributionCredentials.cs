using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Administration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDistributionCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "distribution_credentials",
                schema: "administration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    encrypted_card_number = table.Column<string>(type: "text", nullable: false),
                    card_number_blind_index = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    issuing_authority = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    issued_on = table.Column<DateOnly>(type: "date", nullable: false),
                    expires_on = table.Column<DateOnly>(type: "date", nullable: false),
                    indemnity_insurer = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    indemnity_policy_reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    indemnity_expires_on = table.Column<DateOnly>(type: "date", nullable: true),
                    training_hours = table.Column<int>(type: "integer", nullable: true),
                    training_completed_on = table.Column<DateOnly>(type: "date", nullable: true),
                    valid_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    valid_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_by = table.Column<Guid>(type: "uuid", nullable: true)

                    // NO xmin COLUMN, deliberately, and this line is where it was removed.
                    //
                    // DistributionCredential.Version is mapped onto PostgreSQL's xmin SYSTEM
                    // column (HasColumnName("xmin").HasColumnType("xid")), which is a read-only
                    // binding — but `dotnet ef migrations add` cannot tell that from a schema
                    // change and scaffolds a real column for it. PostgreSQL then refuses the DDL
                    // outright: ERROR: column name "xmin" conflicts with a system column name.
                    //
                    // The symptom is not a failed migration anybody notices: MigrateAsync aborts
                    // at start-up, the table is simply absent, and the first visible effect is an
                    // unrelated endpoint answering a swallowed cross-module error code.
                    //
                    // Renaming it is NOT the fix — see XminIsNeverScaffoldedTests, which is what
                    // caught this one, for why a real `version xid` column would compile, pass
                    // every test and silently retire optimistic concurrency.
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_distribution_credentials", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_distribution_credentials_current",
                schema: "administration",
                table: "distribution_credentials",
                columns: new[] { "tenant_id", "user_id" },
                filter: "valid_to IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_distribution_credentials_expiry",
                schema: "administration",
                table: "distribution_credentials",
                columns: new[] { "tenant_id", "expires_on" });

            migrationBuilder.CreateIndex(
                name: "ux_distribution_credentials_card_number",
                schema: "administration",
                table: "distribution_credentials",
                columns: new[] { "tenant_id", "card_number_blind_index" },
                unique: true,
                filter: "valid_to IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_distribution_credentials_one_current",
                schema: "administration",
                table: "distribution_credentials",
                columns: new[] { "tenant_id", "user_id" },
                unique: true,
                filter: "valid_to IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "distribution_credentials",
                schema: "administration");
        }
    }
}
