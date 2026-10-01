using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Customers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class NormalizeClientPreferredLanguage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Data-only: the column already exists and the model did not change, so EF scaffolded
            // an empty migration. Client.DefaultLanguage now lower-cases the value and keeps only
            // its primary subtag, but rows written before that still hold "FR", "Fr" or "fr-FR" —
            // and those resolve no email template, because template locales are stored lower-case
            // and the lookup runs in PostgreSQL, where string equality is case-sensitive. Such a
            // client is mailed the message's own JSON payload as its body.
            migrationBuilder.Sql("""
                UPDATE customers.clients
                   SET preferred_language = lower(regexp_replace(preferred_language, '[-_].*$', ''))
                 WHERE preferred_language IS NOT NULL
                   AND preferred_language <> lower(regexp_replace(preferred_language, '[-_].*$', ''));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not reversible, deliberately: "fr" carries no record of whether it was typed "FR" or
            // "fr-FR", and restoring a casing that broke email rendering would be a regression
            // rather than a rollback.
        }
    }
}
