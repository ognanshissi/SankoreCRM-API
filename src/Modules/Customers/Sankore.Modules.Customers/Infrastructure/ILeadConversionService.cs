namespace Sankore.Modules.Customers.Infrastructure;

using Sankore.Modules.Customers.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// Seam between the module facade and the lead-conversion slice (US-M01-BE-06).
///
/// <c>Features/LeadConversion</c> implements this interface — it wraps
/// <c>CreateClientFromLeadCommand</c> and its handler, which own the whole
/// conversion policy (idempotency on the source lead, advisor inheritance,
/// encryption of the incoming clear-text identity fields, contact points,
/// initial KYC state). The facade depends on this narrow contract rather than
/// on MediatR so that <c>Infrastructure</c> never has to reference a feature
/// type, and so the facade can be unit-tested without a MediatR pipeline.
/// </summary>
internal interface ILeadConversionService
{
    Task<Result<CreateFromLeadResult>> CreateFromLeadAsync(CreateFromLeadRequest request, CancellationToken ct);

    /// <summary>
    /// The draft holding a converted lead leaves behind (BNF-B-24). Same seam and same reason:
    /// dispatching through MediatR is what puts it behind the validation, transaction and audit
    /// behaviours.
    /// </summary>
    Task<Result<Guid>> CreateHoldingFromLeadAsync(
        CreateHoldingFromLeadRequest request, CancellationToken ct);

    /// <summary>
    /// The draft holding a WON request leaves behind. Same seam and same reason as its lead twin.
    /// </summary>
    Task<Result<Guid>> CreateHoldingFromOpportunityAsync(
        CreateHoldingFromOpportunityRequest request, CancellationToken ct);
}
