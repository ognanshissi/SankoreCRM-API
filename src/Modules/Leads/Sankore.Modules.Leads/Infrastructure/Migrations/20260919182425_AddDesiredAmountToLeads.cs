using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <summary>
    /// Historically a DUPLICATE, now idempotent.
    ///
    /// <para>
    /// <c>BackfillMissingLeadColumns</c>, created seven minutes earlier, already adds
    /// <c>leads.desired_amount</c> with the identical definition — <c>numeric(18,4)</c>, nullable.
    /// This migration's comment said the column "was missed", which was true of
    /// <c>AddLeadCoreV2</c> and no longer true by the time it was written. Applying both in order
    /// therefore failed with <c>42701: column "desired_amount" of relation "leads" already
    /// exists</c>, and since migrations run at start-up, <b>no empty database could be
    /// provisioned</b>: a new deployment and any schema rebuild both died here. Existing databases
    /// never noticed, because this migration had already failed on them and the one before it had
    /// done the work.
    /// </para>
    ///
    /// <para>
    /// Kept rather than deleted: its id may already sit in <c>__EFMigrationsHistory</c> somewhere,
    /// and removing a recorded migration makes EF report the database as ahead of the model.
    /// </para>
    ///
    /// <para>
    /// Raw idempotent SQL rather than an empty <c>Up</c>. Empty would be correct for a database that
    /// applied the previous migration, and wrong for one whose schema had drifted — which is the
    /// exact situation <c>BackfillMissingLeadColumns</c> was written for, so such databases are
    /// known to exist. <c>IF NOT EXISTS</c> is right in both worlds.
    /// </para>
    /// </summary>
    public partial class AddDesiredAmountToLeads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // desired_amount is the Amount half of the Money owned type; desired_currency is the
            // other and was migrated by AddLeadCoreV2.
            migrationBuilder.Sql(
                """
                ALTER TABLE leads.leads
                    ADD COLUMN IF NOT EXISTS desired_amount numeric(18,4) NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately nothing. The column belongs to BackfillMissingLeadColumns, whose own Down
            // drops it; dropping it here would delete a column this migration did not create and
            // leave a database reverted to that point inconsistent with its own history.
        }
    }
}
