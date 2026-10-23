using Sankore.Shared.Kernel;

namespace Sankore.Admin.Domain;

/// <summary>A preset of paid modules chosen when the platform creates a tenant.</summary>
public sealed record TenantProfile(string Code, string Label, string Description, IReadOnlyList<string> Modules);

/// <summary>
/// The four creation profiles of the product decision (§5.5). A profile is only a preset: it fills
/// the tenant's modules at creation as direct activations, and nothing remembers it afterwards —
/// the tenant then asks for or deactivates modules like any other.
/// </summary>
public static class TenantProfiles
{
    public const string ImfOrBank = "ImfOrBank";
    public const string CaisseWithInsurance = "CaisseWithInsurance";
    public const string InsuranceDistributor = "InsuranceDistributor";
    public const string CrmOnly = "CrmOnly";

    public static readonly IReadOnlyList<TenantProfile> All =
    [
        new(ImfOrBank, "IMF ou banque",
            "Core banking, groupes solidaires et produits bancaires.",
            [TenantModules.BankingMicrofinance]),
        new(CaisseWithInsurance, "Caisse avec assurance",
            "Les produits bancaires et la distribution d'assurance.",
            [TenantModules.BankingMicrofinance, TenantModules.Insurance]),
        new(InsuranceDistributor, "Distributeur d'assurance",
            "La distribution d'assurance, sans core banking.",
            [TenantModules.Insurance]),
        new(CrmOnly, "CRM seul",
            "Le socle : leads, clients, conformité, téléphonie et produits standard.",
            []),
    ];

    public static TenantProfile? Find(string? code) =>
        All.FirstOrDefault(p => string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The modules a creation request asks for: the profile's, or the explicit list, never both.
    /// Returns an error code instead when the request is contradictory or names an unknown profile.
    /// </summary>
    public static (IReadOnlyList<string>? Modules, string? Error) ResolveModules(
        string? profile, IReadOnlyList<string>? activeModules)
    {
        if (string.IsNullOrWhiteSpace(profile)) return (activeModules, null);
        if (activeModules is { Count: > 0 }) return (null, "PROFILE_AND_MODULES");
        return Find(profile) is { } found ? (found.Modules, null) : (null, "PROFILE_UNKNOWN");
    }
}
