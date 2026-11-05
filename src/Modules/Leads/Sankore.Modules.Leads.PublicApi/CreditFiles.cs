namespace Sankore.Modules.Leads.PublicApi;

using Sankore.Shared.Kernel;

/// <summary>
/// A credit file's project reached its approval: the core banking is to receive it. Ids only.
/// Outside <c>Leads.PublicApi.Events</c>, the public lifecycle partners may subscribe to: this is a
/// message between two modules.
/// </summary>
public sealed record CreditFileApprovedEvent(Guid TenantId, Guid ProjectId) : IntegrationEventBase;

/// <param name="ClientId">Null when the project has no client yet.</param>
/// <param name="ProductLineId">The catalogue line; null when the product is not in the catalogue.</param>
/// <param name="TermMonths">Null when the field is empty or does not read as a positive number.</param>
public sealed record CreditApplicationFacts(
    Guid ProjectId,
    Guid? ClientId,
    Guid? ProductLineId,
    decimal? Amount,
    string? Currency,
    int? TermMonths,
    string? Purpose);
