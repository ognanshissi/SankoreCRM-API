namespace Sankore.Modules.Kyc.Features.Files.CreateKycFile;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

/// <summary>
/// Opens the one KYC file a customer is entitled to (KYC-B-01).
///
/// Two independent triggers reach it for the same customer: M01 publishes <c>ClientCreatedEvent</c>
/// for every client, and M13 publishes <c>KycRequestedIntegrationEvent</c> when a lead is
/// converted — a converted lead therefore produces BOTH. Add an operator clicking the endpoint and
/// a broker redelivering, and concurrent duplicate calls are the normal case, not the edge one.
///
/// Hence two layers: a read that covers the ordinary race, and the filtered unique index
/// <c>ux_kyc_files_open_per_customer</c> that covers the simultaneous one. The second is what
/// actually guarantees it — the read can be overtaken between its query and its commit.
/// </summary>
internal sealed class CreateKycFileHandler(
    KycDbContext db,
    ICustomersModule customers,
    TimeProvider clock,
    [FromKeyedServices(nameof(KycDbContext))] IEventPublisher publisher,
    ILogger<CreateKycFileHandler> logger)
    : IRequestHandler<CreateKycFileCommand, Result<CreateKycFileResult>>
{
    public async Task<Result<CreateKycFileResult>> Handle(
        CreateKycFileCommand cmd, CancellationToken ct)
    {
        // IgnoreQueryFilters + explicit tenant: consumers run outside any HTTP context, so the
        // ambient tenant is not the one being processed.
        var existing = await db.KycFiles
            .IgnoreQueryFilters()
            .Where(f => f.TenantId == cmd.TenantId
                     && f.CustomerId == cmd.CustomerId
                     && f.Status != KycFileStatus.Rejected
                     && f.Status != KycFileStatus.Suspended)
            .Select(f => (Guid?)f.Id)
            .FirstOrDefaultAsync(ct);

        if (existing is not null)
            return Result.Ok(new CreateKycFileResult(existing.Value, AlreadyExisted: true));

        var file = KycFile.Open(
            tenantId: cmd.TenantId,
            customerId: cmd.CustomerId,
            channel: cmd.Channel,
            createdBy: cmd.InitiatedBy,
            clock: clock,
            vigilanceLevel: cmd.VigilanceLevel,
            agencyId: await ResolveAgencyAsync(cmd, ct));

        db.KycFiles.Add(file);

        // Outbox row written into THIS DbContext so it commits with the file: an event without its
        // file would notify an agent about nothing, and a file without its event would never be
        // announced.
        await publisher.PublishAsync(
            new KycInitiatedEvent(
                TenantId: cmd.TenantId,
                CustomerEntityId: cmd.CustomerId,
                KycFileId: file.Id,
                Channel: cmd.Channel.ToString(),
                InitiatedBy: cmd.InitiatedBy),
            ct);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsOpenFileUniqueViolation(ex))
        {
            // Lost the race. The caller's intent — "this customer must have a KYC file" — is
            // satisfied by the winner, so this is a success carrying the winner's id, not an
            // error an agent should ever see.
            logger.LogInformation(
                "Concurrent KYC file creation for customer {CustomerId}; keeping the existing one",
                cmd.CustomerId);

            var winner = await db.KycFiles
                .IgnoreQueryFilters()
                .Where(f => f.TenantId == cmd.TenantId
                         && f.CustomerId == cmd.CustomerId
                         && f.Status != KycFileStatus.Rejected
                         && f.Status != KycFileStatus.Suspended)
                .Select(f => (Guid?)f.Id)
                .FirstOrDefaultAsync(ct);

            return winner is not null
                ? Result.Ok(new CreateKycFileResult(winner.Value, AlreadyExisted: true))
                // The violation was real but the row is gone: something else closed it between
                // the two statements. Report the code rather than invent an id.
                : Result.Fail<CreateKycFileResult>(KycErrors.FileAlreadyExists);
        }

        return Result.Ok(new CreateKycFileResult(file.Id, AlreadyExisted: false));
    }

    /// <summary>
    /// The customer's agency, copied once so the perimeter can be a SQL predicate forever after.
    ///
    /// <para>
    /// Read from M01's contract and not from the creating agent: a file belongs to the branch that
    /// holds the CUSTOMER, not to whoever happened to open it. A head-office compliance officer
    /// opening a file for a branch customer must not move that file into head office.
    /// </para>
    ///
    /// <para>
    /// A customer M01 cannot resolve yields <c>null</c> and a warning, never a failure. The two
    /// triggers of this command are M01's own event and a lead conversion, so a missing client is
    /// either a race we must not lose or an archived one — and refusing to open the file would
    /// leave MassTransit redelivering forever over a field that is only used for filtering. The
    /// consequence of the null is documented on <see cref="KycFile.AgencyId"/>: restricted callers
    /// do not see the file, which fails closed.
    /// </para>
    /// </summary>
    private async Task<Guid?> ResolveAgencyAsync(CreateKycFileCommand cmd, CancellationToken ct)
    {
        var summary = await customers.GetClientSummaryAsync(cmd.TenantId, cmd.CustomerId, ct);

        if (summary is not null) return summary.AgencyId;

        logger.LogWarning(
            "Opening KYC file for customer {CustomerId} without an agency: M01 returned no summary. "
            + "The file will only be visible to unrestricted users until it is backfilled.",
            cmd.CustomerId);

        return null;
    }

    /// <summary>
    /// Narrow on purpose: only the open-file index may be swallowed. Any other constraint
    /// violation is a bug and must keep propagating instead of being reported as "already exists".
    /// </summary>
    private static bool IsOpenFileUniqueViolation(DbUpdateException ex)
        => ex.InnerException?.Message.Contains("ux_kyc_files_open_per_customer",
               StringComparison.OrdinalIgnoreCase) == true;
}
