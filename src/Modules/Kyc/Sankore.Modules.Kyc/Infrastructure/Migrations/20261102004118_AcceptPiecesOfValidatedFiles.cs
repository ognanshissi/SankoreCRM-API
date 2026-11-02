using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Kyc.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AcceptPiecesOfValidatedFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Files validated before the final approval accepted their pieces (2026-11-02): the
            // newest piece of each kind uploaded before the validation, still Pending, is accepted
            // by whoever signed the last approval (SYSTEM for an automatic one), at the validation.
            migrationBuilder.Sql("""
                UPDATE kyc.kyc_documents d
                SET review_decision = 'Accepted',
                    reviewed_by = COALESCE(
                        (SELECT s.approver_id FROM kyc.kyc_approval_steps s
                         WHERE s.kyc_file_id = f.id AND s.decision = 'Approved'
                         ORDER BY s.decided_at DESC LIMIT 1),
                        '00000000-0000-0000-0000-000000000000'),
                    reviewed_at = f.validated_at
                FROM kyc.kyc_files f
                WHERE d.kyc_file_id = f.id
                  AND f.validated_at IS NOT NULL
                  AND d.review_decision = 'Pending'
                  AND d.uploaded_at <= f.validated_at
                  AND NOT EXISTS (
                      SELECT 1 FROM kyc.kyc_documents n
                      WHERE n.kyc_file_id = d.kyc_file_id AND n.kind = d.kind AND n.uploaded_at > d.uploaded_at);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
