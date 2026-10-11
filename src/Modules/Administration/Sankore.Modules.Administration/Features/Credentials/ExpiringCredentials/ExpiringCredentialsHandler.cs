namespace Sankore.Modules.Administration.Features.Credentials.ExpiringCredentials;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Shared.Kernel;
using Sankore.Modules.Administration.Infrastructure;

internal sealed class ExpiringCredentialsHandler(AdministrationDbContext db, TimeProvider clock)
    : IRequestHandler<ExpiringCredentialsQuery, Result<IReadOnlyList<ExpiringCredentialDto>>>
{
    public async Task<Result<IReadOnlyList<ExpiringCredentialDto>>> Handle(
        ExpiringCredentialsQuery query, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        // Negative is clamped rather than refused: a caller asking for "what has already lapsed"
        // means zero.
        var horizon = today.AddDays(Math.Max(0, query.WithinDays));

        // Current rows only: a superseded card's expiry is history, and reporting it would fill
        // the list with cards that were replaced years ago. Indexed on (tenant_id, expires_on).
        var rows = await db.DistributionCredentials
            .Where(c => c.ValidTo == null
                     && (c.ExpiresOn <= horizon
                         // A lapsed indemnity cover invalidates the credential even while the
                         // card is in date. It is the half an institution does not watch, so the
                         // report has to surface it rather than only sorting by card expiry.
                         || (c.IndemnityExpiresOn != null && c.IndemnityExpiresOn <= horizon)))
            .OrderBy(c => c.ExpiresOn)
            .ToListAsync(ct);

        return Result.Ok<IReadOnlyList<ExpiringCredentialDto>>(
        [
            .. rows.Select(c => new ExpiringCredentialDto(
                CredentialId: c.Id,
                UserId: c.UserId,
                Category: c.Category.ToString(),
                ExpiresOn: c.ExpiresOn,
                DaysUntilExpiry: c.ExpiresOn.DayNumber - today.DayNumber,
                IndemnityExpiresOn: c.IndemnityExpiresOn,
                IndemnityLapsed: c.IndemnityExpiresOn is { } until && until < today,
                // Through the aggregate's own definition, not a comparison repeated here.
                IsValidToday: c.IsValidAt(today))),
        ]);
    }
}
