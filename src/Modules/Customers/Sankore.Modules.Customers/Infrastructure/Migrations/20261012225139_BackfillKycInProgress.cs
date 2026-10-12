using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Customers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BackfillKycInProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Rattrapage : avant KycInitiatedConsumer, un client au dossier KYC ouvert restait
            // « NotStarted ». Lecture directe du schéma kyc, une seule fois et seulement s'il existe
            // déjà — sur une base neuve il n'y a rien à rattraper. Mêmes règles que
            // Client.ApplyKycInitiated : on n'avance que ce qui n'est pas tranché.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF to_regclass('kyc.kyc_files') IS NOT NULL THEN
                        UPDATE customers.clients c
                        SET kyc_status = 'InProgress'
                        WHERE c.kyc_status IN ('NotStarted', 'Rejected', 'Expired')
                          AND EXISTS (
                              SELECT 1 FROM kyc.kyc_files f
                              WHERE f.tenant_id = c.tenant_id
                                AND f.customer_id = c.id
                                AND f.status IN ('Collecting', 'Verifying', 'Validating', 'ComplementRequired'));
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
