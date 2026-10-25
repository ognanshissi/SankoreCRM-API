namespace Sankore.Modules.Customers.Features.Compliance.Erasure;

using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Compliance.Retention;
using Sankore.Modules.Customers.Features.Compliance.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Modules.Leads.PublicApi;

/// <param name="Reference">Client number or « Lead 1a2b3c4d » — never the name.</param>
/// <param name="ClientId">The client the person is — the subject itself, or the client a lead belongs to.</param>
/// <param name="Blocker">Why the erasure cannot run today (a <see cref="CustomerErrors"/> code); null when it can.</param>
/// <param name="EligibleFrom">When it becomes possible, if known: archive date plus the retention.</param>
internal sealed record ErasureVerdict(
    bool Found, bool AlreadyErased, string Reference, Guid? ClientId, string? Blocker, DateTimeOffset? EligibleFrom)
{
    public bool Eligible => Found && !AlreadyErased && Blocker is null;

    public static readonly ErasureVerdict NotFound = new(false, false, "", null, null, null);
}

/// <summary>
/// Whether a person can be forgotten now (data-purge lot 2). ONE answer for the preview, the
/// request, the approval and the daily job — an approval must not see a different rule than the
/// screen that proposed it.
///
/// <para>
/// A prospect (a lead whose person never became a client) can. A client can once its legal
/// retention (LBC/FT) is over: archived, <c>retention-years</c> elapsed since — the same
/// <see cref="RetentionWindow"/> as the monthly proposal and <c>POST clients/{id}/anonymize</c> —
/// and KYC released. A lead of a client is the client.
/// </para>
/// </summary>
internal sealed class ErasureEligibility(
    CustomersDbContext db,
    ICustomerSettings settings,
    IKycModule kyc,
    TimeProvider clock,
    ILeadsModule? leads = null)
{
    public async Task<ErasureVerdict> EvaluateAsync(
        Guid tenantId, ErasureSubjectType subjectType, Guid subjectId, CancellationToken ct)
    {
        if (subjectType == ErasureSubjectType.Client)
            return await ClientAsync(tenantId, subjectId, ct);

        if (leads is null) return ErasureVerdict.NotFound;

        LeadErasureSubject? lead;
        // Suppressed: Leads reads through its own connection, never enlisted in this command's
        // transaction — Npgsql refuses the distributed promotion.
        using (new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled))
            lead = await leads.GetErasureSubjectAsync(tenantId, subjectId, ct);

        if (lead is null) return ErasureVerdict.NotFound;
        if (lead.IsErased) return new(true, true, lead.Reference, lead.ClientId, null, null);
        if (lead.ClientId is not { } clientId) return new(true, false, lead.Reference, null, null, null);

        // The person is a client: the client's retention decides, the lead's reference is kept.
        return await ClientAsync(tenantId, clientId, ct) with { Reference = lead.Reference };
    }

    private async Task<ErasureVerdict> ClientAsync(Guid tenantId, Guid clientId, CancellationToken ct)
    {
        var client = await db.Clients
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId && c.Id == clientId)
            .Select(c => new { c.ClientNumber, c.Status, c.ArchivedAt, c.IsAnonymized })
            .FirstOrDefaultAsync(ct);

        if (client is null) return ErasureVerdict.NotFound;
        if (client.IsAnonymized) return new(true, true, client.ClientNumber, clientId, null, null);

        if (client.Status != ClientStatus.Archived || client.ArchivedAt is not { } archivedAt)
            return new(true, false, client.ClientNumber, clientId, CustomerErrors.ClientRelationshipActive, null);

        var years = await RetentionWindow.ResolveAsync(settings, tenantId, ct);
        var eligibleFrom = archivedAt.AddYears(years);
        if (eligibleFrom > clock.GetUtcNow())
            return new(true, false, client.ClientNumber, clientId, CustomerErrors.RetentionNotReached, eligibleFrom);

        bool cleared;
        using (new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled))
            cleared = await kyc.IsRetentionClearedAsync(tenantId, clientId, ct);
        if (!cleared)
            return new(true, false, client.ClientNumber, clientId, CustomerErrors.KycRetentionNotCleared, eligibleFrom);

        return new(true, false, client.ClientNumber, clientId, null, eligibleFrom);
    }
}
