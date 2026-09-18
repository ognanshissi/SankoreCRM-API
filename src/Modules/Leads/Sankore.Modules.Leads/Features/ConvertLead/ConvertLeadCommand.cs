namespace Sankore.Modules.Leads.Features.ConvertLead;

using MediatR;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Converts a lead into a customer.
/// If <see cref="CustomerId"/> is null the handler generates a new Guid that
/// the Customers module will use as the customer's Id when it processes the
/// <c>LeadConvertedIntegrationEvent</c> from the outbox.
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
    /// <summary>True when high-confidence duplicate leads were found at conversion time.</summary>
    bool DuplicateDetected = false,
    /// <summary>
    /// Populated when <see cref="DuplicateDetected"/> is true.
    /// In Block mode (Force=false) the conversion did NOT proceed — re-submit with Force=true.
    /// </summary>
    IReadOnlyList<DuplicateMatchResult>? PotentialDuplicates = null);
