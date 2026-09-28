namespace Sankore.Modules.Customers.Features.Clients.UpdateClient;

using System.Globalization;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

/// <summary>
/// US-M01-BE-07. Guard order matters and is part of the specification:
/// existence/perimeter → version → read-only.
///
/// Optimistic concurrency is checked TWICE on purpose. The pre-check gives a clean
/// <c>CONCURRENCY_CONFLICT</c> (and a clear audit entry) when the caller's version is
/// already stale at dispatch time; the <c>catch</c> covers the much narrower race where
/// another transaction commits between the read and our own <c>SaveChanges</c>, which
/// only PostgreSQL's <c>xmin</c> token can detect.
/// </summary>
internal sealed class UpdateClientHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    IFieldEncryptor encryptor
) : IRequestHandler<UpdateClientCommand, Result>
{
    public async Task<Result> Handle(UpdateClientCommand cmd, CancellationToken ct)
    {
        var tenantId = currentUser.TenantId;
        var actor = currentUser.Id;

        // AsTracking: the context is NoTracking by default, and this path mutates.
        var client = await db.Clients
            .AsTracking()
            .FirstOrDefaultAsync(c => c.Id == cmd.ClientId, ct);

        if (client is null)
            return Result.Fail(CustomerErrors.ClientNotFound);

        // Outside the caller's agency perimeter reads as NOT FOUND, never as forbidden:
        // a 403 here would confirm that this client exists in another branch, which is
        // itself information the caller is not entitled to.
        if (!await agencyScope.CanAccessAgencyAsync(tenantId, actor, client.AgencyId, ct))
            return Result.Fail(CustomerErrors.ClientNotFound);

        if (client.Version != cmd.ExpectedVersion)
            return Result.Fail(CustomerErrors.ConcurrencyConflict);

        // Checked here as well as inside the aggregate so the guard order above holds
        // even if UpdateNonSensitive's own checks are reordered later.
        if (client.IsReadOnly)
            return Result.Fail(CustomerErrors.ClientReadOnly);

        // Invariant culture so the ciphertext round-trips to the same number whatever
        // the server's locale.
        var encryptedDeclaredIncome = cmd.DeclaredIncome.HasValue
            ? encryptor.Encrypt(cmd.DeclaredIncome.Value.ToString(CultureInfo.InvariantCulture))
            : null;

        var update = client.UpdateNonSensitive(
            profession: cmd.Profession,
            employer: cmd.Employer,
            maritalStatus: cmd.MaritalStatus,
            encryptedDeclaredIncome: encryptedDeclaredIncome,
            declaredIncomeCurrency: cmd.DeclaredIncomeCurrency,
            preferredLanguage: cmd.PreferredLanguage,
            actor: actor);

        if (update.IsFailure)
            return update;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Someone committed between our read and our write: the row's xmin moved.
            return Result.Fail(CustomerErrors.ConcurrencyConflict);
        }

        return Result.Ok();
    }
}
