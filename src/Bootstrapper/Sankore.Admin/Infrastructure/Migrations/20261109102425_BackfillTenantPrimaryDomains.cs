using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Admin.Infrastructure.Migrations
{
    /// <summary>
    /// Gives every tenant (twins aside) the primary <c>TenantDomains</c> row of its main origin.
    /// Tenants created from the platform had none — only the start-up seeder wrote one — so their main
    /// address was missing from their own « Domaines » list. Creation and update now keep it
    /// (<c>TenantPrimaryDomain</c>); this brings the existing tenants in line. An origin already
    /// held by another tenant's row is left alone (unique index), as is a twin (its FQDN is unresolvable).
    /// </summary>
    public partial class BackfillTenantPrimaryDomains : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Primary = the main origin: demote any other primary row of the tenant (kept, still resolvable)…
            migrationBuilder.Sql("""
                UPDATE "TenantDomains" d
                SET "IsPrimary" = false, "UpdatedAt" = now()
                FROM "Tenants" t
                WHERE d."TenantId" = t."Id" AND t."ParentTenantId" IS NULL
                  AND d."IsPrimary" AND d."Fqdn" <> t."Fqdn";
                """);

            // …promote the tenant's own row for it when there is one…
            migrationBuilder.Sql("""
                UPDATE "TenantDomains" d
                SET "IsPrimary" = true, "UpdatedAt" = now()
                FROM "Tenants" t
                WHERE d."TenantId" = t."Id" AND t."ParentTenantId" IS NULL
                  AND NOT d."IsPrimary" AND d."Fqdn" = t."Fqdn";
                """);

            // …and add it otherwise. Both columns are citext: the comparisons ignore case.
            migrationBuilder.Sql("""
                INSERT INTO "TenantDomains" ("Id", "TenantId", "Fqdn", "IsPrimary", "IsActive", "ValidFrom", "ValidTo", "CreatedAt", "UpdatedAt")
                SELECT gen_random_uuid(), t."Id", lower(t."Fqdn"::text), true, true, t."CreatedAt", NULL, now(), now()
                FROM "Tenants" t
                WHERE t."ParentTenantId" IS NULL
                  AND NOT EXISTS (SELECT 1 FROM "TenantDomains" d WHERE d."Fqdn" = t."Fqdn")
                ON CONFLICT ("Fqdn") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo safely: the added rows cannot be told apart from rows written by hand.
        }
    }
}
