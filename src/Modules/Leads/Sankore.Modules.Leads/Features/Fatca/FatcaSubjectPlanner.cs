namespace Sankore.Modules.Leads.Features.Fatca;

using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel;

/// <summary>Who owes a FATCA declaration, and whether FATCA applies at all (BNF-B-11).</summary>
/// <param name="Enabled">
/// From the tenant's compliance settings. When false, NOTHING is required, exposed or counted —
/// BNF-B-11 is explicit, and it is why every caller takes this flag rather than assuming.
/// </param>
internal sealed record FatcaScope(
    bool Enabled,
    bool RequiredAtEnrichment,
    IReadOnlyList<(QualificationSubjectType Type, Guid Id)> Subjects);

/// <summary>
/// Resolves the tenant's FATCA configuration and the people it applies to.
/// </summary>
internal sealed class FatcaSubjectPlanner(IAdministrationModule administration)
{
    internal const string NotEnabled = "FATCA_NOT_ENABLED";
    internal const string SettingsUnavailable = "COMPLIANCE_SETTINGS_UNAVAILABLE";

    /// <summary>
    /// The scope for one lead.
    /// </summary>
    /// <remarks>
    /// <b>Co-holders are the active household</b> (BNF-B-11: "collecte étendue aux co-titulaires si
    /// FATCA_FOR_CO_HOLDERS"). In a lead there is no account yet and therefore no co-titulaire in
    /// the banking sense — the people who will be on it are the household, so that is who the flag
    /// extends collection to. When it is off, only the principal declares.
    ///
    /// <para>
    /// FAILS the caller on an outage rather than assuming a default: whether an institution is
    /// subject to FATCA is not something to guess at, in either direction. Guessing ON would
    /// collect a US TIN from somebody whose institution has no obligation to hold one; guessing OFF
    /// would skip a declaration that is legally required.
    /// </para>
    /// </remarks>
    internal async Task<Result<FatcaScope>> ResolveAsync(Lead lead, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lead);

        ComplianceSettingsDto settings;
        try
        {
            using var suppressed = new System.Transactions.TransactionScope(
                System.Transactions.TransactionScopeOption.Suppress,
                System.Transactions.TransactionScopeAsyncFlowOption.Enabled);

            settings = await administration.GetComplianceSettingsAsync(lead.TenantId, ct);
            suppressed.Complete();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result.Fail<FatcaScope>(SettingsUnavailable);
        }

        if (!settings.FatcaEnabled)
            return Result.Ok(new FatcaScope(false, false, []));

        var subjects = new List<(QualificationSubjectType, Guid)>
        {
            // The principal is identified by the lead's own id — it has none of its own.
            (QualificationSubjectType.Principal, lead.Id),
        };

        if (settings.FatcaForCoHolders)
        {
            subjects.AddRange(lead.ActiveHouseholdMembers
                .Select(m => (QualificationSubjectType.HouseholdMember, m.Id)));
        }

        return Result.Ok(new FatcaScope(
            Enabled: true,
            RequiredAtEnrichment: string.Equals(
                settings.FatcaRequiredAt, "Enrichment", StringComparison.OrdinalIgnoreCase),
            Subjects: subjects));
    }
}
