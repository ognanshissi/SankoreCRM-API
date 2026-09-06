using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Api.Infrastructure.Audit
{
    /// <inheritdoc />
    public partial class ImmutableEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Scenario 2 of US-M12-AUDIT-001: audit.entries must be immutable.
            // A PostgreSQL trigger rejects any UPDATE, DELETE or TRUNCATE at the
            // database level, independently of application-layer controls.
            // This is verified by the AuditImmutabilityTests integration test.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION audit.prevent_mutation()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION
                        'audit.entries is append-only — UPDATE and DELETE are not permitted (SQLSTATE 42501)';
                END;
                $$;

                CREATE TRIGGER trg_audit_entries_no_update
                BEFORE UPDATE ON audit.entries
                FOR EACH ROW EXECUTE FUNCTION audit.prevent_mutation();

                CREATE TRIGGER trg_audit_entries_no_delete
                BEFORE DELETE ON audit.entries
                FOR EACH ROW EXECUTE FUNCTION audit.prevent_mutation();

                CREATE TRIGGER trg_audit_entries_no_truncate
                BEFORE TRUNCATE ON audit.entries
                FOR EACH STATEMENT EXECUTE FUNCTION audit.prevent_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_audit_entries_no_update ON audit.entries;
                DROP TRIGGER IF EXISTS trg_audit_entries_no_delete ON audit.entries;
                DROP TRIGGER IF EXISTS trg_audit_entries_no_truncate ON audit.entries;
                DROP FUNCTION IF EXISTS audit.prevent_mutation();
                """);
        }
    }
}
