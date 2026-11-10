using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.PublicApi;

/// <summary>
/// The institution's KYC switch changed (docs/kyc-par-institution.md), through the Administration
/// outbox. Published only on a REAL change. Customers activates the clients waiting for their KYC
/// when it goes off; M02 and the tasks follow (lot 2). The tenant is the team's (production), the
/// twin following it.
/// </summary>
/// <param name="ActorId">Who switched it; <see cref="Guid.Empty"/> when a module turned it back on.</param>
/// <param name="RestoredByModule">The module whose activation turned KYC back on, or null.</param>
public sealed record TenantKycSwitchedEvent(
    Guid TenantId,
    bool Enabled,
    Guid ActorId,
    string? RestoredByModule = null) : IIntegrationEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}
