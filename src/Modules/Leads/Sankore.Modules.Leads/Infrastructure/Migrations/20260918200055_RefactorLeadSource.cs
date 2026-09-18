using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RefactorLeadSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Rename WalkIn → Agency (in-branch capture)
            migrationBuilder.Sql("UPDATE leads.leads SET source = 'Agency'     WHERE source = 'WalkIn';");

            // Rename InboundCall → CallCenter
            migrationBuilder.Sql("UPDATE leads.leads SET source = 'CallCenter' WHERE source = 'InboundCall';");

            // Consolidate SmsUssdCampaign and MarketingCampaign → Campaign
            migrationBuilder.Sql("UPDATE leads.leads SET source = 'Campaign' WHERE source = 'SmsUssdCampaign';");
            migrationBuilder.Sql("UPDATE leads.leads SET source = 'Campaign' WHERE source = 'MarketingCampaign';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse: Agency → WalkIn
            migrationBuilder.Sql("UPDATE leads.leads SET source = 'WalkIn'          WHERE source = 'Agency';");

            // Reverse: CallCenter → InboundCall
            migrationBuilder.Sql("UPDATE leads.leads SET source = 'InboundCall'     WHERE source = 'CallCenter';");

            // Reverse: Sms / Ussd / Campaign → SmsUssdCampaign (best-effort, information loss accepted)
            migrationBuilder.Sql("UPDATE leads.leads SET source = 'SmsUssdCampaign' WHERE source IN ('Sms', 'Ussd', 'Campaign');");
        }
    }
}
