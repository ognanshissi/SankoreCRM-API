namespace Sankore.Modules.Customers.Features.Lifecycle;

/// <summary>
/// Asks the modules that carry financial commitments whether a client still has
/// anything open — an outstanding loan instalment, a blocked savings account, a
/// pending tontine cycle — so that M01 can refuse to archive a client who is
/// still financially engaged.
///
/// <para>
/// EXTENSION POINT for M03 (Savings) and M04 (Credit). The probe exists today so
/// that <c>ArchiveClientHandler</c> already asks the question through a stable
/// contract: when M03/M04 publish their PublicApi, the only change needed is to
/// register an implementation that fans out to them in
/// <c>LifecycleServiceRegistration</c> — the calling handler, its error code
/// (<c>CLIENT_HAS_ACTIVE_COMMITMENTS</c>) and its tests stay untouched.
/// </para>
/// </summary>
public interface IOutstandingBalanceProbe
{
    /// <summary>
    /// True when at least one module reports an active commitment for this client.
    /// Implementations must be conservative: when a downstream module cannot be
    /// reached, returning <c>true</c> (block the archive) is the safe answer —
    /// archiving is reversible only through a support intervention.
    /// </summary>
    Task<bool> HasActiveCommitmentsAsync(Guid tenantId, Guid clientId, CancellationToken ct);
}
