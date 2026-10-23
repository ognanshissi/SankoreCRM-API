namespace Sankore.Shared.Kernel;

/// <summary>
/// The paid modules a tenant subscribes to, by code. The codes are stored by the tenant registry
/// (Sankore.Admin) and travel on <see cref="TenantInfo"/>, so they are a contract: never rename one.
///
/// <para>
/// Everything that belongs to neither module is the socle and is always on — telephony and
/// compliance (KYC, simplified KYC and its caps, PEP, FATCA, AML) included, by the product
/// owner's decision. Code asks « is this module active? », never « what kind of institution is
/// this? »: an institution may hold both modules.
/// </para>
///
/// <para>
/// Not to be confused with <see cref="ApplicationModules"/>, which names the technical modules a
/// permission belongs to.
/// </para>
/// </summary>
public static class TenantModules
{
    /// <summary>« Banking - Microfinance » : core banking integration, client groups, banking categories.</summary>
    public const string BankingMicrofinance = "BankingMicrofinance";

    /// <summary>« Assurance » : insurer integration, claims, health questionnaires, insurance categories.</summary>
    public const string Insurance = "Insurance";

    public static readonly IReadOnlyList<string> All = [BankingMicrofinance, Insurance];

    /// <summary>Case-sensitive on purpose: a code is an identifier, not a label.</summary>
    public static bool IsKnown(string? code) => code is not null && All.Contains(code);
}
