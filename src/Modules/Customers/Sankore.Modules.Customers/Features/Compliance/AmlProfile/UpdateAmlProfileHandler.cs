namespace Sankore.Modules.Customers.Features.Compliance.AmlProfile;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

/// <summary>
/// Same guard order as every other client write of this module: existence and perimeter, then
/// version, then read-only. Not a shortcut worth taking — a compliance field on an archived
/// client is still a write to an archived client.
/// </summary>
internal sealed class UpdateAmlProfileHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    IFieldEncryptor encryptor)
    : IRequestHandler<UpdateAmlProfileCommand, Result>
{
    public async Task<Result> Handle(UpdateAmlProfileCommand cmd, CancellationToken ct)
    {
        var tenantId = currentUser.TenantId;
        var actor = currentUser.Id;

        var client = await db.Clients
            .AsTracking()
            .FirstOrDefaultAsync(c => c.Id == cmd.ClientId, ct);

        if (client is null)
            return Result.Fail(CustomerErrors.ClientNotFound);

        // Outside the perimeter reads as NOT FOUND, never forbidden: a 403 would confirm this
        // client exists in another branch.
        if (!await agencyScope.CanAccessAgencyAsync(tenantId, actor, client.AgencyId, ct))
            return Result.Fail(CustomerErrors.ClientNotFound);

        if (client.Version != cmd.ExpectedVersion)
            return Result.Fail(CustomerErrors.ConcurrencyConflict);

        if (client.IsReadOnly)
            return Result.Fail(CustomerErrors.ClientReadOnly);

        // Blank clears, so an empty narrative must reach the aggregate as an empty STRING rather
        // than as encrypted emptiness — "nothing recorded" has to read as null in the column.
        var encryptedDetail = cmd.FundsOriginDetail switch
        {
            null => null,
            "" => string.Empty,
            var detail => encryptor.Encrypt(detail),
        };

        var update = client.UpdateAmlProfile(
            activitySectorCode: cmd.ActivitySectorCode,
            activitySectorDetail: cmd.ActivitySectorDetail,
            fundsOriginCode: cmd.FundsOriginCode,
            encryptedFundsOriginDetail: encryptedDetail,
            corporatePurpose: cmd.CorporatePurpose,
            registrationExtractIssuedOn: cmd.RegistrationExtractIssuedOn,
            actor: actor);

        if (update.IsFailure)
            return update;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Fail(CustomerErrors.ConcurrencyConflict);
        }

        return Result.Ok();
    }
}
