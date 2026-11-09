using Microsoft.Extensions.Hosting;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Integration.Infrastructure;

/// <summary>
/// Who may reach the in-memory adapter (<see cref="PublicApi.IntegrationKind.Fake"/>).
///
/// <para>
/// INT-10 made it Development-only, and the reason stands: it answers every write with a success
/// and an invented reference, so a REAL institution reaching it would believe accounts were opened
/// and policies subscribed while no back office ever heard of them. The owner's choice of
/// 2026-11-08 (<c>docs/donnees-demarrage-demo.md</c>, lot 3) keeps that for real institutions and
/// opens it to TEST ones on every platform, so a demo on the production platform can subscribe a
/// policy end to end: a test institution has no back office to disagree with.
/// </para>
///
/// <para>
/// Asked when a connection of that kind is created, when it is activated, and EVERY time a
/// connection is resolved — the last one is the guarantee: an institution whose test flag is lifted
/// stops reaching the double at its next call, whatever connections it kept.
/// </para>
/// </summary>
internal sealed class FakeAdapterGate(IHostEnvironment environment, ITenantStore tenants)
{
    public async Task<bool> AllowsAsync(Guid tenantId, CancellationToken ct)
        => environment.IsDevelopment() || (await tenants.GetAsync(tenantId, ct))?.IsTest == true;
}
