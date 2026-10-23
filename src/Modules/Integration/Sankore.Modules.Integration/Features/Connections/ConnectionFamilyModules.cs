namespace Sankore.Modules.Integration.Features.Connections;

using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// The paid module a connection family needs, or null for the socle. Telephony and messaging are
/// socle and never gated. A core banking connection needs Banking - Microfinance and nothing else
/// — a tenant with Insurance alone has no core banking connection, even to debit premiums (product
/// owner, 2026-10-23). Only creating and activating are gated: a connection already active keeps
/// serving the contracts already running.
/// </summary>
internal static class ConnectionFamilyModules
{
    public static string? RequiredModule(IntegrationFamily family) => family switch
    {
        IntegrationFamily.CoreBanking => TenantModules.BankingMicrofinance,
        IntegrationFamily.Insurance => TenantModules.Insurance,
        _ => null,
    };

    public static async Task<bool> IsOpenAsync(ITenantModules modules, IntegrationFamily family, CancellationToken ct)
        => RequiredModule(family) is not { } module || await modules.IsEnabledAsync(module, ct);
}
