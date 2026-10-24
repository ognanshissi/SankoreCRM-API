namespace Sankore.Modules.Leads.Features.ConvertLead;

using MediatR;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Converts a lead into a customer.
///
/// When <see cref="CustomerId"/> is supplied the lead is attached to that existing customer.
/// When it is null the handler CREATES the client synchronously through
/// <c>ICustomersModule.CreateFromLeadAsync</c> (US-M01-BE-06) and uses the identifier it
/// returns. It is deliberately not left to an outbox consumer: the caller needs the customer
/// id in its response, and a lead flagged "converted" pointing at a customer that does not
/// exist yet is worse than a slower call.
/// </summary>
internal sealed record ConvertLeadCommand(
    Guid LeadId,
    Guid? CustomerId = null,
    /// <summary>
    /// When true, bypasses the duplicate gate and forces conversion even when high-confidence
    /// duplicate leads exist.
    /// </summary>
    bool Force = false,
    /// <summary>
    /// Minimum confidence score (0-100) to trigger the duplicate gate at conversion.
    /// Defaults to 70 (Probable) — only high-confidence matches block conversion.
    /// </summary>
    double MinConfidenceThreshold = 70.0,
    /// <summary>
    /// The project the client file is opened for: ITS product decides the KYC and the fields
    /// required (product owner, 2026-10-24 — the lead stores no product line). Null: the lead's
    /// single open project, else what the lead asked for.
    /// </summary>
    Guid? ProjectId = null
) : IRequest<Result<ConvertLeadResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId => LeadId.ToString();
}

public sealed record ConvertLeadResult(
    Guid LeadId,
    Guid CustomerId,
    DateTimeOffset ConvertedAt,
    /// <summary>
    /// Set when M01 refused to create the client because its identity document already belongs
    /// to someone — <c>DUPLICATE_IDENTITY_DOCUMENT</c>. The lead is NOT converted; the agent is
    /// expected to attach it to <see cref="ExistingCustomerId"/> by hand (US-M01-BE-06).
    /// </summary>
    string? BlockingCode = null,
    Guid? ExistingCustomerId = null,
    string? ExistingCustomerNumber = null,
    /// <summary>True when high-confidence duplicate leads were found at conversion time.</summary>
    bool DuplicateDetected = false,
    /// <summary>
    /// Populated when <see cref="DuplicateDetected"/> is true.
    /// In Block mode (Force=false) the conversion did NOT proceed — re-submit with Force=true.
    /// </summary>
    IReadOnlyList<DuplicateMatchResult>? PotentialDuplicates = null,
    /// <summary>
    /// Which of BNF-B-12's two paths ran — <c>NewCustomer</c> or
    /// <c>ExistingCustomerNewProduct</c>. Empty on a refusal, because no path ran.
    /// </summary>
    string ConversionType = "",
    /// <summary>
    /// True when the client file was ALREADY open — or the lead already converted — and this call
    /// changed nothing (BNF-B-24). <see cref="CustomerId"/> is that client. Distinct from
    /// <c>CreateFromLeadResult.AlreadyExisted</c>, which is about the CLIENT already existing: a
    /// first conversion of a re-captured prospect sets that and not this.
    /// </summary>
    bool AlreadyConverted = false,
    /// <summary>
    /// True when the lead IS converted — which happens when one of its projects is approved, never
    /// at this call. A successful call opens the client file and leaves the lead open.
    /// </summary>
    bool LeadConverted = false,
    /// <summary>
    /// The DRAFT product holding the conversion opened on the client (BNF-B-24), or null when the
    /// lead had no resolved product line or M01 declined — the client already holds the line, say.
    /// Null is NOT a conversion failure: the holding is a consequence of the conversion, not a
    /// condition of it, and an agent can open the subscription by hand on the client record.
    /// </summary>
    Guid? HoldingId = null,
    /// <summary>
    /// The completeness report the gate ran (BNF-B-04) — the same object
    /// <c>GET leads/{id}/completeness</c> returns. Populated on a refusal so the agent is told
    /// everything that is missing, not only the first thing, and on success so a screen can show
    /// the file was complete.
    /// </summary>
    Features.Enrichment.LeadCompletenessDto? Completeness = null);
