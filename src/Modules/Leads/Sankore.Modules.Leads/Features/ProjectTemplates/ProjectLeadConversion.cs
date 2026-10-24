namespace Sankore.Modules.Leads.Features.ProjectTemplates;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.ConvertLead;
using Sankore.Modules.Leads.Features.ConvertLead.Events;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Messaging;

/// <summary>
/// Converts a project's lead when the project is APPROVED — product owner, 2026-10-24: « un lead
/// est converti lorsqu'un projet a été approuvé ». Approved means reaching the step marked
/// « Approbation », or winning the project when its template has none.
///
/// <para>
/// <b>Nothing to decide here, only to record.</b> <see cref="ProjectStepGate"/> has already refused
/// an approval without a client (and without the KYC the product asks), and the client file was
/// opened earlier from the lead (<c>ConvertLeadHandler</c>), which attached the lead to it. This
/// marks the lead converted to that client, in the same transaction as the move that approved the
/// project, and publishes <see cref="LeadConvertedIntegrationEvent"/> through the outbox.
/// </para>
///
/// <para>
/// A lead already converted — by another project — or closed (lost, disqualified, archived) is left
/// as it is: the project goes on, and reopening a lead is an agent's act, not a side effect.
/// </para>
/// </summary>
internal sealed class ProjectLeadConversion(
    LeadsDbContext db,
    [FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher publisher)
{
    /// <summary>Whether reaching <paramref name="targetIndex"/> approves the project.</summary>
    /// <param name="targetIndex"><c>Steps.Count</c> for a project being won.</param>
    public static bool Approves(ResolvedProjectTemplate template, int targetIndex)
    {
        var approval = template.Steps.ToList().FindIndex(s => s.IsApproval);
        return targetIndex >= template.Steps.Count || (approval >= 0 && targetIndex >= approval);
    }

    /// <summary>
    /// Converts the lead if this move approves the project. Does NOT save: the caller commits the
    /// move and the conversion together.
    /// </summary>
    public async Task ConvertIfApprovedAsync(
        Opportunity project, ResolvedProjectTemplate template, int targetIndex, CancellationToken ct)
    {
        if (project.LeadId is not { } leadId || !Approves(template, targetIndex))
            return;

        var lead = await db.Leads.AsTracking().FirstOrDefaultAsync(l => l.Id == leadId, ct);
        if (lead is null
            || lead.Status is LeadStatus.Converted or LeadStatus.Lost or LeadStatus.Disqualified or LeadStatus.Archived)
            return;

        // The gate guarantees a client on an approval; nothing to convert to otherwise.
        if ((lead.CustomerId ?? project.CustomerEntityId) is not { } clientId)
            return;

        if (lead.Convert(clientId).IsFailure)
            return;

        // The lead's other projects follow it onto the client record, like at the file opening.
        var others = await db.Opportunities
            .AsTracking()
            .Where(o => o.LeadId == lead.Id && o.CustomerEntityId == null)
            .ToListAsync(ct);
        foreach (var other in others)
            other.AttachToCustomer(clientId, lead.ConvertedAt!.Value);

        await publisher.PublishAsync(
            new LeadConvertedIntegrationEvent(
                LeadId:      lead.Id,
                CustomerId:  clientId,
                TenantId:    lead.TenantId,
                FullName:    lead.FullName,
                PhoneNumber: lead.PhoneNumber,
                Email:       lead.Email,
                ConvertedAt: lead.ConvertedAt!.Value,
                // A client this lead's own file opening created is a new customer; one the lead was
                // attached to otherwise already existed.
                ConversionType: lead.CustomerLinkReason == ConvertLeadHandler.ClientFileOpenedReason
                    ? LeadConversionType.NewCustomer
                    : LeadConversionType.ExistingCustomerNewProduct,
                ProductLineId:  project.ProductLineId),
            ct);
    }
}
