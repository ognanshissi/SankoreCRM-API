using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Kyc.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddApprovalStepLevelRank : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "level_rank",
                schema: "kyc",
                table: "kyc_approval_steps",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // BACKFILL, and not optional. The default of 0 sits BELOW Agent (1), so every circuit
            // written before this column would report its next pending rung as rank 0 — a rank no
            // role maps to, and every one of those files would silently stop appearing as awaiting
            // anybody. The rank is a projection of the level, so it is derived from the level that
            // is already there.
            //
            // The CASE lists the levels that exist at this migration's date on purpose: it is a
            // one-off repair of history, not a rule. A level added later is ranked by the domain
            // factory, which is the only thing that writes this column from now on.
            migrationBuilder.Sql(
                """
                UPDATE kyc.kyc_approval_steps
                   SET level_rank = CASE level
                                        WHEN 'Agent'             THEN 1
                                        WHEN 'BranchManager'     THEN 2
                                        WHEN 'ComplianceOfficer' THEN 3
                                        ELSE 0
                                    END
                 WHERE level_rank = 0;
                """);

            migrationBuilder.CreateIndex(
                name: "ix_kyc_approval_steps_pending_rank",
                schema: "kyc",
                table: "kyc_approval_steps",
                columns: new[] { "kyc_file_id", "decision", "level_rank" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_kyc_approval_steps_pending_rank",
                schema: "kyc",
                table: "kyc_approval_steps");

            migrationBuilder.DropColumn(
                name: "level_rank",
                schema: "kyc",
                table: "kyc_approval_steps");
        }
    }
}
