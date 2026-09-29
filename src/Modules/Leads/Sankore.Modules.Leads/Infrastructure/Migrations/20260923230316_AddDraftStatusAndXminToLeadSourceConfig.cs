using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDraftStatusAndXminToLeadSourceConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty.
            //
            // EF scaffolded an AddColumn for "xmin" because LeadSourceConfig maps a concurrency
            // token onto it. That DDL cannot run on PostgreSQL:
            //     ERROR: column name "xmin" conflicts with a system column name
            // xmin is the transaction id Postgres stamps on every row version — it exists on
            // each table already, so mapping it is a binding, not a schema change.
            //
            // The Draft status this migration is also named after needed no DDL either: the
            // status is stored as text, so a new enum member changes nothing in the schema.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing was added, and a system column could not be dropped in any case.
        }
    }
}
