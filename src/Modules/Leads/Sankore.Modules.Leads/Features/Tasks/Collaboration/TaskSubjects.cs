namespace Sankore.Modules.Leads.Features.Tasks.Collaboration;

using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Modules.Leads.Features.Visibility;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

/// <summary>What a task is about, as shown: the type, the id and a readable name.</summary>
public sealed record TaskSubjectDto(string Type, Guid Id, string Label);

/// <summary>
/// The record a task is attached to: checked at creation — it exists and the creator may work on
/// it — and named for display. The lead is M13's own; a client and a KYC file are opaque
/// references read through their module's contract, outside the caller's transaction.
///
/// <para>
/// A subject the creator cannot reach answers the same « not found » as one that does not exist,
/// as every module here does: a task form is not a way to probe another branch's records.
/// </para>
/// </summary>
internal sealed class TaskSubjects(
    LeadsDbContext db,
    ICurrentUser me,
    LeadAccessGuard leadAccess,
    ICustomersModule customers,
    IKycModule kyc,
    LeadVisibility visibility)
{
    /// <summary>The id to store — a merged client resolves to its survivor — or the refusal.</summary>
    public async Task<Result<Guid>> ValidateAsync(TaskEntityType type, Guid id, CancellationToken ct)
    {
        switch (type)
        {
            case TaskEntityType.Lead:
            {
                var lead = await db.Leads.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, ct);
                if (lead is null) return Result.Fail<Guid>("LEAD_NOT_FOUND");
                var work = await leadAccess.EnsureCanWorkAsync(lead, ct);
                return work.IsSuccess ? Result.Ok(id) : Result.Fail<Guid>("LEAD_NOT_FOUND");
            }

            case TaskEntityType.Client:
            {
                using var suppressed = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
                var resolved = await customers.ResolveClientIdAsync(me.TenantId, id, ct);
                if (resolved is not { } clientId || !await customers.CanActorAccessClientAsync(me.TenantId, clientId, me.Id, ct))
                    return Result.Fail<Guid>("CLIENT_NOT_FOUND");
                return Result.Ok(clientId);
            }

            case TaskEntityType.KycFile:
            {
                using var suppressed = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
                return await kyc.CanActorAccessKycFileAsync(me.TenantId, id, me.Id, ct)
                    ? Result.Ok(id)
                    : Result.Fail<Guid>("KYC_FILE_NOT_FOUND");
            }

            case TaskEntityType.Project:
            {
                // A closed project still takes tasks: a won sale has a contract to sign after it.
                var visible = await visibility.Apply(db.Opportunities, db.Leads).AnyAsync(o => o.Id == id, ct);
                return visible ? Result.Ok(id) : Result.Fail<Guid>("OPPORTUNITY_NOT_FOUND");
            }

            default:
                return Result.Fail<Guid>("TASK_ENTITY_TYPE_UNKNOWN");
        }
    }

    /// <summary>The name to show. A record gone since keeps its type and a neutral label.</summary>
    public async Task<TaskSubjectDto?> DescribeAsync(CrmTask task, CancellationToken ct)
    {
        if (task.EntityType is not { } type || task.EntityId is not { } id) return null;

        try
        {
            switch (type)
            {
                case TaskEntityType.Lead:
                    var name = await db.Leads.AsNoTracking().Where(l => l.Id == id).Select(l => l.FullName).FirstOrDefaultAsync(ct);
                    return new TaskSubjectDto("Lead", id, string.IsNullOrWhiteSpace(name) ? "Lead" : name);

                case TaskEntityType.Client:
                {
                    using var suppressed = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
                    var client = await customers.GetClientSummaryAsync(task.TenantId, id, ct);
                    return new TaskSubjectDto("Client", id, client?.DisplayName ?? "Client");
                }

                case TaskEntityType.KycFile:
                {
                    using var suppressed = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
                    var file = await kyc.GetKycFileSummaryAsync(task.TenantId, id, ct);
                    var client = file is null ? null : await customers.GetClientSummaryAsync(task.TenantId, file.CustomerId, ct);
                    return new TaskSubjectDto("KycFile", id, client is null ? "Dossier KYC" : $"Dossier KYC · {client.DisplayName}");
                }

                case TaskEntityType.Project:
                    var title = await db.Opportunities.AsNoTracking().Where(o => o.Id == id).Select(o => o.Title).FirstOrDefaultAsync(ct);
                    return new TaskSubjectDto("Project", id, string.IsNullOrWhiteSpace(title) ? "Projet" : title);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A module down must not hide the task: the subject keeps its type, without a name.
        }

        return new TaskSubjectDto(type.ToString(), id, type switch
        {
            TaskEntityType.Lead => "Lead",
            TaskEntityType.Client => "Client",
            TaskEntityType.Project => "Projet",
            _ => "Dossier KYC",
        });
    }
}
