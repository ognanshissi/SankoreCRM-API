using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Administration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationSettingsConcurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty.
            //
            // EF scaffolded an AddColumn for "xmin" because the entity maps a concurrency token
            // onto it. That DDL would FAIL on PostgreSQL:
            //     ERROR: column name "xmin" conflicts with a system column name
            // xmin already exists on every table — it is the transaction id Postgres stamps on
            // each row version. Mapping it is a read-only binding, not a schema change, so there
            // is nothing to apply. The model snapshot keeps the mapping, which is what matters
            // at run time.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing was added, so there is nothing to drop — and dropping a system column is
            // not possible anyway.
        }
    }
}
