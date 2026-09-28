namespace Sankore.Modules.Customers.Features.Duplicates.Merge.RequestClientMerge;

using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Workflow.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

/// <summary>
/// Records the intent to merge two clients and, best effort, opens the matching M12 workflow
/// instance so the approval shows up in the approver's task list.
/// </summary>
internal sealed class RequestClientMergeHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    IWorkflowModule workflow,
    ILogger<RequestClientMergeHandler> logger)
    : IRequestHandler<RequestClientMergeCommand, Result<RequestClientMergeResult>>
{
    /// <summary>Entity type M12 keys its template on.</summary>
    private const string WorkflowEntityType = "ClientMerge";

    public async Task<Result<RequestClientMergeResult>> Handle(
        RequestClientMergeCommand command, CancellationToken ct)
    {
        if (command.SurvivorClientId == command.AbsorbedClientId)
            return Result.Fail<RequestClientMergeResult>(CustomerErrors.SameClientMergeForbidden);

        if (string.IsNullOrWhiteSpace(command.Reason))
            return Result.Fail<RequestClientMergeResult>(CustomerErrors.ReasonRequired);

        var tenantId = currentUser.TenantId;

        var clients = await db.Clients
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId
                     && (c.Id == command.SurvivorClientId || c.Id == command.AbsorbedClientId))
            .Select(c => new { c.Id, c.AgencyId, c.Status })
            .ToListAsync(ct);

        var survivor = clients.FirstOrDefault(c => c.Id == command.SurvivorClientId);
        var absorbed = clients.FirstOrDefault(c => c.Id == command.AbsorbedClientId);

        if (survivor is null || absorbed is null)
            return Result.Fail<RequestClientMergeResult>(CustomerErrors.ClientNotFound);

        // The agency perimeter is enforced on BOTH clients: a merge rewrites two records, so seeing
        // only one of them is not enough to ask for it. Out of perimeter answers CLIENT_NOT_FOUND
        // (404, never 403) so the caller cannot probe for clients of another branch.
        foreach (var client in clients)
        {
            if (!await agencyScope.CanAccessAgencyAsync(tenantId, currentUser.Id, client.AgencyId, ct))
                return Result.Fail<RequestClientMergeResult>(CustomerErrors.ClientNotFound);
        }

        if (absorbed.Status == ClientStatus.Merged)
            return Result.Fail<RequestClientMergeResult>(CustomerErrors.ClientAlreadyMerged);

        if (survivor.Status is ClientStatus.Merged or ClientStatus.Archived)
            return Result.Fail<RequestClientMergeResult>(CustomerErrors.ClientReadOnly);

        var fieldChoicesJson = JsonSerializer.Serialize(
            command.FieldChoices ?? new Dictionary<string, string>());

        var request = ClientMergeRequest.Open(
            tenantId, command.SurvivorClientId, command.AbsorbedClientId, fieldChoicesJson, currentUser.Id);

        db.ClientMergeRequests.Add(request);

        // M12 gives the approver a task and an audit trail of the approval chain, but it is NOT what
        // guarantees the four-eyes rule: the engine enforces no self-approval check, so M01 keeps
        // that rule in ApproveClientMergeHandler. A missing template or an unavailable workflow
        // module therefore degrades gracefully — the request stands, WorkflowInstanceId stays null.
        Guid? workflowInstanceId = null;
        try
        {
            var started = await workflow.StartWorkflowAsync(
                new WorkflowStartRequest(WorkflowEntityType, request.Id, currentUser.Id, tenantId), ct);

            if (started.IsSuccess)
            {
                workflowInstanceId = started.Value;
                request.LinkWorkflow(started.Value);
            }
            else
            {
                logger.LogWarning(
                    "No workflow instance for merge request {MergeRequestId} (tenant {TenantId}): {Error}. " +
                    "The request stays pending and the four-eyes approval is enforced by M01.",
                    request.Id, tenantId, started.Error);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Starting the ClientMerge workflow failed for merge request {MergeRequestId} (tenant {TenantId}). " +
                "The request stays pending and the four-eyes approval is enforced by M01.",
                request.Id, tenantId);
        }

        await db.SaveChangesAsync(ct);

        return Result.Ok(new RequestClientMergeResult(
            request.Id, request.Status.ToString(), workflowInstanceId));
    }
}
