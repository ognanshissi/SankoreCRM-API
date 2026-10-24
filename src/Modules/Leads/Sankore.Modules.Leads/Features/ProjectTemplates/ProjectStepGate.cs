namespace Sankore.Modules.Leads.Features.ProjectTemplates;

using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

/// <summary>
/// What a project needs before it reaches a marked step of its template (lot 3).
///
/// <list type="bullet">
/// <item><b>« Ouvre le dossier client »</b> — a client record. For a product that requires KYC,
/// this is where it opens: the agent converts the lead (the conversion opens the KYC file), and a
/// lead already converted whose client has no file yet gets one requested here.</item>
/// <item><b>« Approbation »</b>, and winning the project — a client, and for a product that
/// requires KYC, a file APPROVED at the tier the product asks for. For a product that requires
/// none (a Standard line whose policy says None), only the client: it is created active when the
/// agent converts at this step, which is the product owner's rule — a no-KYC customer exists once
/// the project is approved, not before.</item>
/// </list>
///
/// <para>
/// <b>Refuses, never converts by itself.</b> A conversion can stop on things only the agent can
/// settle — an incomplete file, a probable duplicate, an identity document already on someone
/// else — so the gate answers <c>PROJECT_CLIENT_FILE_REQUIRED</c> and the screen opens the
/// conversion the agent already knows. A template with no marked step gates nothing, which is
/// every tenant that has not configured one: the built-in default marks none.
/// </para>
///
/// <para>
/// <b>Fail-closed on the KYC question</b>: an unresolved product, an unreadable policy or an
/// unreachable KYC module all read as « KYC required, not approved ».
/// </para>
/// </summary>
internal sealed class ProjectStepGate(
    LeadsDbContext db,
    IAdministrationModule administration,
    IKycModule kyc,
    ITenantContext tenant,
    ICurrentUser currentUser,
    [FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher publisher)
{
    /// <param name="targetIndex">
    /// The index of the step reached in <paramref name="template"/>; <c>Steps.Count</c> for a project
    /// being won, which reaches every step.
    /// </param>
    /// <param name="currentIndex">Where the project stands now, -1 when nowhere (closed).</param>
    public async Task<Result> CheckAsync(
        Opportunity project,
        ResolvedProjectTemplate template,
        int currentIndex,
        int targetIndex,
        CancellationToken ct)
    {
        var steps = template.Steps.ToList();
        var opens = steps.FindIndex(s => s.OpensClientFile);
        var approval = steps.FindIndex(s => s.IsApproval);

        var reachesFile = opens >= 0 && targetIndex >= opens;
        var reachesApproval = approval >= 0 && targetIndex >= approval;
        if (!reachesFile && !reachesApproval)
            return Result.Ok();

        var (kycRequired, tier, lineId) = await RequirementOfAsync(project.Product, ct);
        var lead = project.LeadId is { } leadId
            ? await db.Leads.AsNoTracking().FirstOrDefaultAsync(l => l.Id == leadId, ct)
            : null;
        var clientId = project.CustomerEntityId ?? lead?.ConvertedToCustomerId ?? lead?.CustomerId;

        if (reachesApproval)
        {
            if (clientId is null)
                return Result.Fail(ProjectTemplateErrors.ClientFileRequired);

            return kycRequired ? await KycApprovedAsync(clientId.Value, tier, ct) : Result.Ok();
        }

        // The client-file step, below the approval one. Nothing to ask of a no-KYC product: its
        // client is created when the project is approved.
        if (!kycRequired)
            return Result.Ok();

        if (clientId is null)
            return Result.Fail(ProjectTemplateErrors.ClientFileRequired);

        // Crossing the step with a client that has no KYC file yet — a client created earlier on a
        // product that needed none, say: ask M02 for one, on this project's product line. The
        // consumer is idempotent, so a second crossing asks for nothing new.
        if (lead is not null && currentIndex < opens && await KycStatusAsync(clientId.Value, ct) is null or KycStatus.NotStarted)
        {
            await publisher.PublishAsync(
                new KycRequestedIntegrationEvent(
                    TenantId:         tenant.CurrentTenantId,
                    CustomerEntityId: clientId.Value,
                    LeadId:           lead.Id,
                    FullName:         lead.FullName,
                    PhoneNumber:      lead.PhoneNumber,
                    Email:            lead.Email,
                    NationalId:       lead.NationalId,
                    DateOfBirth:      lead.DateOfBirth,
                    RequestedBy:      currentUser.Id,
                    HasPepExposure:   lead.HasPepExposure,
                    ProductLineId:    lineId),
                ct);
        }

        return Result.Ok();
    }

    private async Task<Result> KycApprovedAsync(Guid clientId, string? tier, CancellationToken ct)
    {
        try
        {
            using var suppressed = new TransactionScope(
                TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);

            var status = await kyc.GetStatusAsync(tenant.CurrentTenantId, clientId, ct);
            if (status != KycStatus.Approved)
                return Result.Fail(ProjectTemplateErrors.KycNotApproved);

            if (tier is not null)
            {
                var limits = await kyc.GetLimitsAsync(tenant.CurrentTenantId, clientId, ct);
                if (limits is null || !string.Equals(limits.Tier, tier, StringComparison.OrdinalIgnoreCase))
                    return Result.Fail(ProjectTemplateErrors.KycTierInsufficient);
            }

            suppressed.Complete();
            return Result.Ok();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
            return Result.Fail(ProjectTemplateErrors.KycNotApproved);
        }
    }

    private async Task<KycStatus?> KycStatusAsync(Guid clientId, CancellationToken ct)
    {
        try
        {
            using var suppressed = new TransactionScope(
                TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
            var status = await kyc.GetStatusAsync(tenant.CurrentTenantId, clientId, ct);
            suppressed.Complete();
            return status;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
            // Unknown: asking for a file costs nothing when one exists (the consumer is idempotent).
            return null;
        }
    }

    /// <summary>(KYC required?, tier to compare or null, product line) for a project's product.</summary>
    private async Task<(bool Required, string? Tier, Guid? LineId)> RequirementOfAsync(string? product, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(product))
            return (true, null, null);

        try
        {
            using var suppressed = new TransactionScope(
                TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);

            var lineId = await administration.ResolveProductLineAsync(tenant.CurrentTenantId, product, ct);
            var policy = lineId is { } id
                ? await administration.GetBeneficiaryPolicyAsync(tenant.CurrentTenantId, id, ct)
                : null;

            suppressed.Complete();

            if (policy is null)
                return (true, null, lineId);

            return (KycRequirement.IsRequired(policy.RequiredKycTier), KycRequirement.Tier(policy.RequiredKycTier), lineId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
            return (true, null, null);
        }
    }
}
