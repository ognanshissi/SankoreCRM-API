using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Infrastructure;

namespace Sankore.Modules.Administration.Features.Agencies.CreateAgency;

internal sealed class PostgresAgencyCodeGenerator(AdministrationDbContext db) : IAgencyCodeGenerator
{
    public async Task<string> NextCodeAsync(CancellationToken ct = default)
    {
        var seqVal = await db.Database
            .SqlQuery<long>($"""SELECT nextval('administration.agency_code_seq') AS "Value" """)
            .SingleAsync(ct);

        return $"AG{seqVal:D6}";
    }
}
