using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <summary>
    /// The initial migration created company_name, company_email, company_phone,
    /// company_address and website as NOT NULL, but they are optional in the domain.
    /// </summary>
    public partial class FixNullableOrganisationColumns : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE leads.leads ALTER COLUMN company_name DROP NOT NULL;
                ALTER TABLE leads.leads ALTER COLUMN company_email DROP NOT NULL;
                ALTER TABLE leads.leads ALTER COLUMN company_phone DROP NOT NULL;
                ALTER TABLE leads.leads ALTER COLUMN company_address DROP NOT NULL;
                ALTER TABLE leads.leads ALTER COLUMN website DROP NOT NULL;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE leads.leads SET company_name = '' WHERE company_name IS NULL;
                UPDATE leads.leads SET company_email = '' WHERE company_email IS NULL;
                UPDATE leads.leads SET company_phone = '' WHERE company_phone IS NULL;
                UPDATE leads.leads SET company_address = '' WHERE company_address IS NULL;
                UPDATE leads,leads SET website = '' WHERE website IS NULL;
                ALTER TABLE leads.leads ALTER COLUMN company_name SET NOT NULL;
                ALTER TABLE leads.leads ALTER COLUMN company_email SET NOT NULL;
                ALTER TABLE leads.leads ALTER COLUMN company_phone SET NOT NULL;
                ALTER TABLE leads.leads ALTER COLUMN company_address SET NOT NULL;
                ALTER TABLE leads.leads ALTER COLUMN website DROP NOT NULL;
                """);
        }
    }
}
