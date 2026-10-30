namespace Sankore.Modules.Administration.Features.Credentials.IssueCredential;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Kernel;

/// <summary>
/// Issues or renews a credential. A renewal CLOSES the previous row rather than editing it, in
/// the same transaction, so the register can be read back as it stood on any past date.
/// </summary>
internal sealed class IssueCredentialHandler(
    AdministrationDbContext db,
    [FromKeyedServices(AdministrationFieldProtection.Key)] IFieldEncryptor encryptor,
    [FromKeyedServices(AdministrationFieldProtection.Key)] IBlindIndexer indexer,
    ICurrentUser currentUser,
    TimeProvider clock)
    : IRequestHandler<IssueCredentialCommand, Result<CredentialDto>>
{
    public async Task<Result<CredentialDto>> Handle(
        IssueCredentialCommand command, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        // The query filter does the isolation: a user of another tenant is simply not found, and
        // 404 is the right answer rather than 403 — "forbidden" would confirm the account exists.
        var userExists = await db.Users.AnyAsync(u => u.Id == command.UserId, ct);
        if (!userExists)
            return Result.Fail<CredentialDto>(CredentialErrors.UserNotFound);

        var category = Enum.Parse<DistributionCredentialCategory>(command.Category, ignoreCase: true);

        var blindIndex = indexer.Compute(BlindIndexPurpose.ProfessionalCard, command.CardNumber);

        // A card number belongs to one person. Checked before the write so the caller gets a
        // named refusal rather than a unique-violation 500 — the filtered unique index behind it
        // is what makes the rule true under concurrency.
        var numberHeldByAnother = await db.DistributionCredentials
            .AnyAsync(
                c => c.CardNumberBlindIndex == blindIndex
                  && c.ValidTo == null
                  && c.UserId != command.UserId,
                ct);

        if (numberHeldByAnother)
            return Result.Fail<CredentialDto>(CredentialErrors.DuplicateCardNumber);

        var previous = await db.DistributionCredentials
            .AsTracking()
            .FirstOrDefaultAsync(c => c.UserId == command.UserId && c.ValidTo == null, ct);

        if (previous is not null)
        {
            // A renewal must say what happened to the old card. Without a reason the register
            // cannot tell a lapse from a withdrawal, which is the first thing a controller asks.
            if (string.IsNullOrWhiteSpace(command.SupersedeReason))
                return Result.Fail<CredentialDto>(CredentialErrors.SupersedeReasonRequired);

            var close = previous.Close(command.SupersedeReason, currentUser.Id, now);
            if (close.IsFailure)
                return Result.Fail<CredentialDto>(close.Error!);
        }

        var credential = DistributionCredential.Issue(
            tenantId: currentUser.TeamTenantId,
            userId: command.UserId,
            category: category,
            encryptedCardNumber: encryptor.Encrypt(command.CardNumber)!,
            cardNumberBlindIndex: blindIndex,
            issuingAuthority: command.IssuingAuthority,
            issuedOn: command.IssuedOn,
            expiresOn: command.ExpiresOn,
            createdBy: currentUser.Id,
            now: now,
            indemnityInsurer: command.IndemnityInsurer,
            indemnityPolicyReference: command.IndemnityPolicyReference,
            indemnityExpiresOn: command.IndemnityExpiresOn,
            trainingHours: command.TrainingHours,
            trainingCompletedOn: command.TrainingCompletedOn);

        db.DistributionCredentials.Add(credential);

        // One SaveChanges for the close and the issue: a transaction that closed the old card and
        // failed to record the new one would leave the holder unlicensed by accident.
        await db.SaveChangesAsync(ct);

        return Result.Ok(CredentialDto.From(credential, today));
    }
}
