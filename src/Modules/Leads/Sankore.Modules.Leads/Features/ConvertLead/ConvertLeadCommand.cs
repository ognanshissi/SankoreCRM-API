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
    double MinConfidenceThreshold = 70.0
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
    IReadOnlyList<DuplicateMatchResult>? PotentialDuplicates = null);
