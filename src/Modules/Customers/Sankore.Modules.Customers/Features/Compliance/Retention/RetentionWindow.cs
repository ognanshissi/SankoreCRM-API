namespace Sankore.Modules.Customers.Features.Compliance.Retention;

using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Compliance.Shared;
using Sankore.Modules.Customers.Infrastructure;

/// <summary>
/// The one place that turns the <c>retention-years</c> tenant setting into a cut-off date.
/// <para>
/// The monthly job and the anonymization endpoint must agree exactly: if the job proposed a
/// client the endpoint then refused (or worse, the other way round), the whole feature would be
/// untrustworthy. Both call <see cref="ResolveAsync"/>.
/// </para>
/// </summary>
internal static class RetentionWindow
{
    /// <summary>Timeline entries produced by this zone are tagged as coming from M01 itself.</summary>
    public const string TimelineSourceModule = "Customers";

    /// <summary>Entry type that materializes "this archived client may now be anonymized".</summary>
    public const string RetentionEligibleEntryType = "RETENTION_ELIGIBLE";

    /// <summary>Entry type that records a completed export run (tenant-level, no client).</summary>
    public const string ExportCompletedEntryType = "CLIENT_EXPORT_COMPLETED";

    /// <summary>
    /// Effective retention duration for the tenant, floored at
    /// <see cref="CustomerSettingValueValidation.MinimumRetentionYears"/>.
    /// <para>
    /// The floor is applied on READ as well as on write: the write path refuses a value below
    /// ten years, but a row seeded by an older version — or edited straight in SQL — must not
    /// be able to shorten the legal term either.
    /// </para>
    /// </summary>
    public static async Task<int> ResolveAsync(
        ICustomerSettings settings, Guid tenantId, CancellationToken ct)
    {
        var configured = await settings.GetIntAsync(tenantId, CustomerSettingKeys.RetentionYears, ct);

        return Math.Max(CustomerSettingValueValidation.MinimumRetentionYears, configured);
    }

    /// <summary>
    /// A client archived strictly before this instant has served its retention period.
    /// </summary>
    public static DateTimeOffset CutOff(DateTimeOffset now, int retentionYears) =>
        now.AddYears(-retentionYears);

    /// <summary>
    /// Dedup key of the monthly eligibility entry. Keyed by month, so re-running the job inside
    /// the same month is a no-op while the following month re-proposes the client that was not
    /// acted upon.
    /// </summary>
    public static string EligibilityDedupKey(Guid clientId, DateTimeOffset at) =>
        $"{TimelineSourceModule}:{RetentionEligibleEntryType}:{clientId:D}:{at:yyyy-MM}";
}
