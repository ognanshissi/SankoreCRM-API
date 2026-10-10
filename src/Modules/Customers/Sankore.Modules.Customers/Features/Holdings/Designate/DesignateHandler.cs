namespace Sankore.Modules.Customers.Features.Holdings.Designate;

using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Holdings.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

internal sealed class DesignateHandler(
    HoldingWriteContext context,
    DesignationSubjectValidator subjects,
    [FromKeyedServices(nameof(CustomersDbContext))] IEventPublisher publisher)
    : IRequestHandler<DesignateCommand, Result<DesignateResult>>
{
    public async Task<Result<DesignateResult>> Handle(DesignateCommand cmd, CancellationToken ct)
    {
        var loaded = await context.LoadAsync(cmd.HoldingId, ct);
        if (loaded.IsFailure)
            return Result.Fail<DesignateResult>(loaded.Error!);

        var holding = loaded.Value;

        var line = await context.ReadLineOfAsync(holding, ct);
        if (line.IsFailure)
            return Result.Fail<DesignateResult>(line.Error!);

        var requests = HoldingMapping.ToRequests(cmd.Designations);
        if (requests.IsFailure)
            return Result.Fail<DesignateResult>(requests.Error!);

        // Every subject must be somebody this holding may designate. The aggregate validates the
        // SHAPE of a subject and cannot go further — deciding whether an id is a member of this
        // client's household is a query. Without this, any non-empty Guid was stored: a dangling
        // subject reaches an insurer as a beneficiary with no name, and an id belonging to another
        // client or another tenant would carry that person's identity into the payload.
        var subjectsValid = await subjects.EnsureValidAsync(
            holding.TenantId, holding.ClientId, requests.Value, ct);

        if (subjectsValid.IsFailure)
            return Result.Fail<DesignateResult>(subjectsValid.Error!);

        // Captured BEFORE the write: Designate moves ClauseVersion, so asking afterwards whether
        // this was an amendment would compare the new version with itself.
        var wasActive = holding.Status == HoldingStatus.Active;
        var previousVersion = holding.ClauseVersion;

        var designated = holding.Designate(
            requests.Value, line.Value.AllowedRoles, context.Actor, context.Clock.GetUtcNow());

        if (designated.IsFailure)
            return Result.Fail<DesignateResult>(designated.Error!);

        var version = designated.Value;
        var isAmendment = wasActive && version > previousVersion;

        // Through the outbox, in the clause's own transaction. Ids and the version only — the
        // designations themselves stay in M01, and an outbox row carrying who receives a capital
        // would be a copy of the contract that nothing purges.
        await publisher.PublishAsync(
            new BeneficiaryDesignationChangedEvent(
                TenantId:         holding.TenantId,
                ClientId:         holding.ClientId,
                HoldingId:        holding.Id,
                ClauseVersion:    version,
                DesignationCount: cmd.Designations.Count,
                IsAmendment:      isAmendment,
                RecordedBy:       context.Actor),
            ct);

        await context.SaveAsync(ct);

        return Result.Ok(new DesignateResult(version, cmd.Designations.Count, isAmendment));
    }
}
