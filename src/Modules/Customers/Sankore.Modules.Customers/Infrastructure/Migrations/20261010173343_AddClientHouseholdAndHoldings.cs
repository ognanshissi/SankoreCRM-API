using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Customers.Infrastructure.Migrations
{
    /// <summary>
    /// The client's household on the existing relationship table, plus the two new tables for the
    /// products a client holds and who is designated on them (BNF-B-13/14/15).
    ///
    /// <para>
    /// ADDITIVE only, and the one-spouse unique index is deliberately NOT here — it needs the
    /// existing rows checked first, which is <c>AddOneSpouseConstraint</c>. Splitting them is what
    /// lets this migration be applied without any risk of failing on data it did not create.
    /// </para>
    ///
    /// <para>
    /// Every column added to <c>client_relationships</c> and <c>clients</c> is nullable or has a
    /// default, and no backfill is needed: a <c>pep_declared_at</c> of null means "nobody has
    /// declared a PEP status", which is exactly true of every pre-existing row. Filling it would
    /// manufacture a declaration nobody made.
    /// </para>
    ///
    /// <para>
    /// The <c>ck_client_relationship_union</c> CHECK is added to a populated table and passes on
    /// every existing row for a structural reason: <c>spouse_union_type</c> is created by this same
    /// migration, so it is null everywhere, and the predicate allows that for any type.
    /// </para>
    /// </summary>
    public partial class AddClientHouseholdAndHoldings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "has_pep_exposure",
                schema: "customers",
                table: "clients",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "pep_declared_at",
                schema: "customers",
                table: "clients",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "principal_pep_country",
                schema: "customers",
                table: "clients",
                type: "character(2)",
                fixedLength: true,
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "principal_pep_end_date",
                schema: "customers",
                table: "clients",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "principal_pep_function",
                schema: "customers",
                table: "clients",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "principal_pep_is_exposed",
                schema: "customers",
                table: "clients",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "principal_pep_link",
                schema: "customers",
                table: "clients",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "principal_pep_override_reason",
                schema: "customers",
                table: "clients",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "external_address_encrypted",
                schema: "customers",
                table: "client_relationships",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "external_city",
                schema: "customers",
                table: "client_relationships",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "external_country_code",
                schema: "customers",
                table: "client_relationships",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "external_first_name",
                schema: "customers",
                table: "client_relationships",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "external_gender",
                schema: "customers",
                table: "client_relationships",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "external_last_name",
                schema: "customers",
                table: "client_relationships",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "external_postal_code",
                schema: "customers",
                table: "client_relationships",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "external_usage_name",
                schema: "customers",
                table: "client_relationships",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pep_country",
                schema: "customers",
                table: "client_relationships",
                type: "character(2)",
                fixedLength: true,
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "pep_declared_at",
                schema: "customers",
                table: "client_relationships",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "pep_end_date",
                schema: "customers",
                table: "client_relationships",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pep_function",
                schema: "customers",
                table: "client_relationships",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "pep_is_exposed",
                schema: "customers",
                table: "client_relationships",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "pep_link",
                schema: "customers",
                table: "client_relationships",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pep_override_reason",
                schema: "customers",
                table: "client_relationships",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "source_lead_member_id",
                schema: "customers",
                table: "client_relationships",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "spouse_union_type",
                schema: "customers",
                table: "client_relationships",
                type: "character varying(15)",
                maxLength: 15,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "uses_principal_address",
                schema: "customers",
                table: "client_relationships",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "product_holdings",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    product_category = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    origin = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    specifics = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    source_lead_id = table.Column<Guid>(type: "uuid", nullable: true),
                    signed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    signature_reference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    external_ref = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    external_system = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    synced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    rejection_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    close_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    clause_version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_holdings", x => x.id);
                    table.ForeignKey(
                        name: "fk_product_holdings_clients_client_id",
                        column: x => x.client_id,
                        principalSchema: "customers",
                        principalTable: "clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "beneficiary_designations",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    holding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    subject_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: true),
                    external_full_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    external_relationship = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    rank = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    share_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    status_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    clause_version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    valid_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    valid_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    recorded_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beneficiary_designations", x => x.id);
                    table.CheckConstraint("ck_designation_rank", "rank >= 1");
                    table.CheckConstraint("ck_designation_share", "share_percent IS NULL OR (share_percent >= 0 AND share_percent <= 100)");
                    table.CheckConstraint("ck_designation_subject", "(subject_type = 'ExternalPerson' AND subject_id IS NULL AND external_full_name IS NOT NULL) OR (subject_type <> 'ExternalPerson' AND subject_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_beneficiary_designations_product_holdings_holding_id",
                        column: x => x.holding_id,
                        principalSchema: "customers",
                        principalTable: "product_holdings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_clients_tenant_id_has_pep_exposure",
                schema: "customers",
                table: "clients",
                columns: new[] { "tenant_id", "has_pep_exposure" },
                filter: "has_pep_exposure");

            migrationBuilder.AddCheckConstraint(
                name: "ck_client_relationship_union",
                schema: "customers",
                table: "client_relationships",
                sql: "(type = 'Spouse') OR (spouse_union_type IS NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_beneficiary_designations_holding_id",
                schema: "customers",
                table: "beneficiary_designations",
                column: "holding_id");

            migrationBuilder.CreateIndex(
                name: "ix_beneficiary_designations_tenant_id_holding_id",
                schema: "customers",
                table: "beneficiary_designations",
                columns: new[] { "tenant_id", "holding_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beneficiary_designations_tenant_id_subject_id",
                schema: "customers",
                table: "beneficiary_designations",
                columns: new[] { "tenant_id", "subject_id" },
                filter: "subject_id IS NOT NULL AND valid_to IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_designations_current",
                schema: "customers",
                table: "beneficiary_designations",
                columns: new[] { "tenant_id", "holding_id", "role_code" },
                filter: "valid_to IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_product_holdings_client_id",
                schema: "customers",
                table: "product_holdings",
                column: "client_id");

            migrationBuilder.CreateIndex(
                name: "ix_product_holdings_pending",
                schema: "customers",
                table: "product_holdings",
                columns: new[] { "tenant_id", "status" },
                filter: "status IN ('PendingSync', 'Rejected')");

            migrationBuilder.CreateIndex(
                name: "ix_product_holdings_tenant_id_client_id_status",
                schema: "customers",
                table: "product_holdings",
                columns: new[] { "tenant_id", "client_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_product_holdings_tenant_id_external_ref",
                schema: "customers",
                table: "product_holdings",
                columns: new[] { "tenant_id", "external_ref" },
                filter: "external_ref IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_product_holdings_tenant_id_product_line_id",
                schema: "customers",
                table: "product_holdings",
                columns: new[] { "tenant_id", "product_line_id" });

            migrationBuilder.CreateIndex(
                name: "ux_product_holdings_live_per_line",
                schema: "customers",
                table: "product_holdings",
                columns: new[] { "tenant_id", "client_id", "product_line_id" },
                unique: true,
                filter: "status IN ('Draft', 'PendingSignature', 'PendingSync', 'Active') AND origin = 'CrmSubscription'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "beneficiary_designations",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "product_holdings",
                schema: "customers");

            migrationBuilder.DropIndex(
                name: "ix_clients_tenant_id_has_pep_exposure",
                schema: "customers",
                table: "clients");

            migrationBuilder.DropCheckConstraint(
                name: "ck_client_relationship_union",
                schema: "customers",
                table: "client_relationships");

            migrationBuilder.DropColumn(
                name: "has_pep_exposure",
                schema: "customers",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "pep_declared_at",
                schema: "customers",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "principal_pep_country",
                schema: "customers",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "principal_pep_end_date",
                schema: "customers",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "principal_pep_function",
                schema: "customers",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "principal_pep_is_exposed",
                schema: "customers",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "principal_pep_link",
                schema: "customers",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "principal_pep_override_reason",
                schema: "customers",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "external_address_encrypted",
                schema: "customers",
                table: "client_relationships");

            migrationBuilder.DropColumn(
                name: "external_city",
                schema: "customers",
                table: "client_relationships");

            migrationBuilder.DropColumn(
                name: "external_country_code",
                schema: "customers",
                table: "client_relationships");

            migrationBuilder.DropColumn(
                name: "external_first_name",
                schema: "customers",
                table: "client_relationships");

            migrationBuilder.DropColumn(
                name: "external_gender",
                schema: "customers",
                table: "client_relationships");

            migrationBuilder.DropColumn(
                name: "external_last_name",
                schema: "customers",
                table: "client_relationships");

            migrationBuilder.DropColumn(
                name: "external_postal_code",
                schema: "customers",
                table: "client_relationships");

            migrationBuilder.DropColumn(
                name: "external_usage_name",
                schema: "customers",
                table: "client_relationships");

            migrationBuilder.DropColumn(
                name: "pep_country",
                schema: "customers",
                table: "client_relationships");

            migrationBuilder.DropColumn(
                name: "pep_declared_at",
                schema: "customers",
                table: "client_relationships");

            migrationBuilder.DropColumn(
                name: "pep_end_date",
                schema: "customers",
                table: "client_relationships");

            migrationBuilder.DropColumn(
                name: "pep_function",
                schema: "customers",
                table: "client_relationships");

            migrationBuilder.DropColumn(
                name: "pep_is_exposed",
                schema: "customers",
                table: "client_relationships");

            migrationBuilder.DropColumn(
                name: "pep_link",
                schema: "customers",
                table: "client_relationships");

            migrationBuilder.DropColumn(
                name: "pep_override_reason",
                schema: "customers",
                table: "client_relationships");

            migrationBuilder.DropColumn(
                name: "source_lead_member_id",
                schema: "customers",
                table: "client_relationships");

            migrationBuilder.DropColumn(
                name: "spouse_union_type",
                schema: "customers",
                table: "client_relationships");

            migrationBuilder.DropColumn(
                name: "uses_principal_address",
                schema: "customers",
                table: "client_relationships");
        }
    }
}
