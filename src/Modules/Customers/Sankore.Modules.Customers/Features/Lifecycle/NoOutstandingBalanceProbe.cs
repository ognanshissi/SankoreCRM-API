namespace Sankore.Modules.Customers.Features.Lifecycle;

/// <summary>
/// Default <see cref="IOutstandingBalanceProbe"/> for the current perimeter:
/// no module owning financial commitments is deployed yet (M03 Savings and
/// M04 Credit are not scaffolded), so there is nothing to interrogate and the
/// honest answer is "no known commitment".
///
/// <para>
/// This is intentionally NOT a silent "always allow": the blocking rule of
/// US-M01-BE-15 is already wired in <c>ArchiveClientHandler</c> and returns
/// <c>CLIENT_HAS_ACTIVE_COMMITMENTS</c> the day a real probe answers
/// <c>true</c>. Replacing this registration with an implementation that queries
/// <c>ISavingsModule</c> / <c>ICreditModule</c> activates the rule without a
/// single change to the handler, the endpoint or their tests.
/// </para>
/// </summary>
internal sealed class NoOutstandingBalanceProbe : IOutstandingBalanceProbe
{
    public Task<bool> HasActiveCommitmentsAsync(Guid tenantId, Guid clientId, CancellationToken ct)
        => Task.FromResult(false);
}
