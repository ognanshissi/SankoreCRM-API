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
}
