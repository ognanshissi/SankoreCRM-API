namespace Sankore.Modules.Kyc.Features.Duplicates.ClearDuplicateFlag;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ClearDuplicateFlagHandler(
    KycDbContext db,
    TimeProvider clock,
    ILogger<ClearDuplicateFlagHandler> logger)
    : IRequestHandler<ClearDuplicateFlagCommand, Result>
{
    public async Task<Result> Handle(ClearDuplicateFlagCommand cmd, CancellationToken ct)
    {
        // Re-checked here and not only in the validator: this is the rule the story is about, and
        // a reason-less lift reaching the aggregate through some future caller would be an
        // untraceable compliance decision. The code is what the front translates.
        if (string.IsNullOrWhiteSpace(cmd.Reason))
            return Result.Fail(DuplicateErrors.ClearReasonRequired);

        var file = await db.KycFiles
            .AsTracking()
            .FirstOrDefaultAsync(f => f.Id == cmd.KycFileId, ct);

        if (file is null)
            return Result.Fail(KycErrors.FileNotFound);

        if (!file.DuplicateSuspected)
            // Idempotent rather than an error: two officers clicking on the same alert is the
            // normal case, and the second one has not done anything wrong.
            return Result.Ok();

        file.ClearDuplicateSuspicion(clock);

        // VigilanceLevel is deliberately left where the flag put it. The file WAS once suspect and
        // that stays true: lowering it back would erase the reason the compliance officer was
        // added to the circuit, and the next reviewer would see an ordinary file.
        logger.LogInformation(
            "Duplicate suspicion lifted on KYC file {KycFileId} by {UserId}; vigilance stays {Vigilance}",
            file.Id, cmd.ClearedBy, file.VigilanceLevel);

        await db.SaveChangesAsync(ct);

        return Result.Ok();
    }
}
