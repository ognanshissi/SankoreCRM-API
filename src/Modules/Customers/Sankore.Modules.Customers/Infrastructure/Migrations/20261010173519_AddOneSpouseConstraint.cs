using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Customers.Infrastructure.Migrations
{
    /// <summary>
    /// The second half of BNF-B-13's one-spouse invariant: the CONSTRAINT.
    ///
    /// <para>
    /// Separate from the migration that added the household columns, deliberately.
    /// <c>client_relationships</c> predates the invariant and has never enforced it, so creating
    /// the unique index blind would die with <c>23505 could not create unique index
    /// "uq_client_one_spouse"</c> — a message that names neither the tenant nor the client, on a
    /// deploy that then rolls back with nobody knowing which rows to fix.
    /// </para>
    ///
    /// <para>
    /// So the detection runs FIRST, in the same transaction, and raises a message that names the
    /// offenders. Nothing is repaired automatically: two active spouses on one client is either a
    /// data-entry error or a separation nobody recorded, and only a person can tell which.
    /// </para>
    /// </summary>
    public partial class AddOneSpouseConstraint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── Detection, before the constraint ────────────────────────────
            //
            // The query an operator can also run on its own ahead of the deploy:
            //
            //   SELECT tenant_id, client_id, count(*)
            //   FROM customers.client_relationships
            //   WHERE type = 'Spouse' AND valid_to IS NULL
            //   GROUP BY tenant_id, client_id
            //   HAVING count(*) > 1;
            //
            // Each offending client needs its surplus spouse closed — valid_to plus a reason —
            // through the household endpoint, so the removal is audited like any other.
            migrationBuilder.Sql(@"
                DO $$
                DECLARE offenders text;
                BEGIN
                    SELECT string_agg(format('tenant %s / client %s: %s active spouses',
                                             tenant_id, client_id, n), '; ')
                    INTO offenders
                    FROM (
                        SELECT tenant_id, client_id, count(*) AS n
                        FROM customers.client_relationships
                        WHERE type = 'Spouse' AND valid_to IS NULL
                        GROUP BY tenant_id, client_id
                        HAVING count(*) > 1
                    ) duplicates;

                    IF offenders IS NOT NULL THEN
                        RAISE EXCEPTION 'Cannot create uq_client_one_spouse: %. Close the surplus spouse relationships (valid_to + close_reason) and re-run the migration.', offenders;
                    END IF;
                END $$;
            ");

            migrationBuilder.CreateIndex(
                name: "uq_client_one_spouse",
                schema: "customers",
                table: "client_relationships",
                columns: new[] { "tenant_id", "client_id" },
                unique: true,
                filter: "type = 'Spouse' AND valid_to IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "uq_client_one_spouse",
                schema: "customers",
                table: "client_relationships");
        }
    }
}
