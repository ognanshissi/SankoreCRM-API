using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Admin.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantModules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ModuleDefinitions",
                columns: table => new
                {
                    Code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    MonthlyPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    IsAvailable = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModuleDefinitions", x => x.Code);
                });

            migrationBuilder.CreateTable(
                name: "TenantModuleSubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ModuleCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    AcceptedMonthlyPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    AcceptedCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DecidedByAdminId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecisionReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancelledByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActivatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeactivatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeactivatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeactivatedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantModuleSubscriptions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tenant_module_subscriptions_status",
                table: "TenantModuleSubscriptions",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "ix_tenant_module_subscriptions_tenant_id",
                table: "TenantModuleSubscriptions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ux_tenant_module_open",
                table: "TenantModuleSubscriptions",
                columns: new[] { "TenantId", "ModuleCode" },
                unique: true,
                filter: "\"Status\" IN ('Requested', 'Active')");

            // The offer. Price 0 and NOT available: nobody has priced the modules yet, and a
            // tenant must not be able to accept « 0 XOF ». A platform administrator sets the price
            // and opens each module from the admin front-end (PUT /api/v1/module-catalog/{code}).
            migrationBuilder.Sql("""
                INSERT INTO "ModuleDefinitions" ("Code", "Label", "Description", "MonthlyPrice", "Currency", "IsAvailable", "UpdatedAt")
                VALUES
                  ('BankingMicrofinance', 'Banking - Microfinance',
                   'Intégration core banking, groupes solidaires, tontines et AVEC, catégories de produits bancaires et de microfinance.',
                   0, 'XOF', false, now()),
                  ('Insurance', 'Assurance',
                   'Distribution d''assurance : intégration assureur, souscription, sinistres, réclamations, questionnaires santé et bénéficiaires.',
                   0, 'XOF', false, now());
                """);

            // Every tenant that exists today keeps everything it has: both modules, in force,
            // recorded as Migration. Done here in SQL and NOT by a start-up seeder, so that a
            // tenant created later on the socle alone is never granted both modules by mistake.
            migrationBuilder.Sql("""
                INSERT INTO "TenantModuleSubscriptions"
                    ("Id", "TenantId", "ModuleCode", "Status", "Source", "CreatedAt", "DecidedAt", "ActivatedAt")
                SELECT gen_random_uuid(), t."Id", m.code, 'Active', 'Migration', now(), now(), now()
                FROM "Tenants" t
                CROSS JOIN (VALUES ('BankingMicrofinance'), ('Insurance')) AS m(code);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ModuleDefinitions");

            migrationBuilder.DropTable(
                name: "TenantModuleSubscriptions");
        }
    }
}
