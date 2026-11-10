namespace Sankore.Shared.Kernel;

/// <summary>
/// Whether an institution runs KYC at all (docs/kyc-par-institution.md, product owner 2026-11-10).
///
/// <para>
/// <b>The one question every « must this customer have a KYC file? » starts with.</b> The product
/// then says which tier (or « None » for a Standard line). Here, the institution: its administrator
/// may switch KYC off in socle only, and a paid module whose products are regulated (Banking -
/// Microfinance, Insurance) keeps it in force whatever the switch says.
/// </para>
///
/// <para>
/// The switch is the TEAM's: a « Mode test » twin reads its production's, because running KYC is
/// a posture of the institution, not a setting to try out.
/// </para>
/// </summary>
public interface ITenantKyc
{
    Task<TenantKycState> GetAsync(Guid tenantId, CancellationToken ct = default);
}

/// <param name="SwitchedOn">What the institution's administrator set (true until they switch it off).</param>
/// <param name="RequiredByModule">The active module that keeps KYC in force, or null in socle only.</param>
public sealed record TenantKycState(bool SwitchedOn, string? RequiredByModule)
{
    /// <summary>What a gate reads. An unknown answer is in force: a check that cannot tell must not waive KYC.</summary>
    public static readonly TenantKycState InForceByDefault = new(true, null);

    public bool InForce => SwitchedOn || RequiredByModule is not null;
}

public static class TenantKycExtensions
{
    public static async Task<bool> IsInForceAsync(this ITenantKyc kyc, Guid tenantId, CancellationToken ct = default)
        => (await kyc.GetAsync(tenantId, ct)).InForce;

    /// <summary>
    /// The modules whose products a regulator looks at: while one is active the institution keeps
    /// its KYC. Order = the one named to the administrator when both are.
    /// </summary>
    public static readonly IReadOnlyList<string> RegulatedModules =
        [TenantModules.BankingMicrofinance, TenantModules.Insurance];

    /// <summary>The first regulated module among <paramref name="active"/>; null = unknown reads as all.</summary>
    public static string? RegulatedModuleIn(IReadOnlyCollection<string>? active)
        => active is null
            ? RegulatedModules[0]
            : RegulatedModules.FirstOrDefault(active.Contains);
}

/// <summary>A fixed answer, for tests and tooling.</summary>
public sealed class FixedTenantKyc(TenantKycState state) : ITenantKyc
{
    public static FixedTenantKyc InForce { get; } = new(TenantKycState.InForceByDefault);

    public static FixedTenantKyc Off { get; } = new(new TenantKycState(false, null));

    public Task<TenantKycState> GetAsync(Guid tenantId, CancellationToken ct = default) => Task.FromResult(state);
}

/// <summary>Error codes of the KYC switch. Relayed to the front-end as they are: never rename one.</summary>
public static class TenantKycErrors
{
    /// <summary>Switching KYC off while a regulated module is active.</summary>
    public const string RequiredByModule = "KYC_REQUIRED_BY_MODULE";

    /// <summary>A KYC write on an institution whose KYC is not in force.</summary>
    public const string Disabled = "KYC_DISABLED";
}
