namespace Sankore.Modules.Kyc.Features.Reviews;

using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Infrastructure.Settings;

/// <summary>
/// Turns a vigilance level into a review date and plants the <see cref="KycReviewSchedule"/> row
/// (KYC-B-07).
///
/// <para>
/// The periodicity is a tenant parameter, not a constant: the BCEAO graduated approach sets a
/// floor, a microfinance institution may review more often, and the compliance officer changes it
/// from the settings screen without a deployment. Every caller therefore goes through here instead
/// of adding years itself — one place computes a due date, so one place is wrong when the
/// regulation moves.
/// </para>
///
/// <para>
/// Nothing here calls <c>SaveChangesAsync</c>. The schedule row and whatever caused it — an
/// approval, a transaction alert — must commit together or not at all, and the caller owns that
/// transaction (a command handler inside <c>TransactionBehavior</c>, or the review job). The
/// <see cref="KycFile"/> passed in must be TRACKED, otherwise the
/// <see cref="KycFile.ScheduleNextReview"/> below is written to an object nobody saves.
/// </para>
/// </summary>
internal sealed class KycReviewScheduler(KycDbContext db, IKycSettings settings, TimeProvider clock)
{
    /// <summary>
    /// Validation date + N years, N read from the tenant's settings for the file's vigilance
    /// level: High → 1 year, Standard → 3, Low → 5 by default.
    /// </summary>
    public async Task<DateOnly> ComputeDueDateAsync(
        Guid tenantId, KycVigilanceLevel level, DateOnly validatedOn, CancellationToken ct)
    {
        var years = await settings.GetIntAsync(tenantId, SettingKeyFor(level), ct);

        // A zero or negative periodicity is a misconfiguration, never an intent: it would make
        // every file due the day it is approved. One year is the strictest periodicity the
        // regulation knows, so that is the floor we fall back to rather than a date in the past.
        return validatedOn.AddYears(Math.Max(1, years));
    }

    /// <summary>
    /// The periodic review that follows an approval or a completed review. Overwrites the file's
    /// cached <see cref="KycFile.NextReviewDate"/>: this IS the new deadline, and it is normally
    /// later than the one that has just been honoured.
    /// </summary>
    public async Task<KycReviewSchedule> SchedulePeriodicAsync(
        KycFile file, DateOnly validatedOn, CancellationToken ct)
    {
        var due = await ComputeDueDateAsync(file.TenantId, file.VigilanceLevel, validatedOn, ct);
        return Append(file, due, KycReviewTrigger.Periodic, reason: null, overwriteNextReview: true);
    }

    /// <summary>
    /// A review raised by something that happened — a transaction alert, a new document, a change
    /// of beneficial owner — without waiting for the periodic deadline.
    ///
    /// <paramref name="reason"/> is shown to an operator and stored in clear, so it must never
    /// carry a sensitive value (no document number, no phone, no address).
    /// </summary>
    public KycReviewSchedule ScheduleEvent(KycFile file, DateOnly dueDate, string reason)
        => Append(file, dueDate, KycReviewTrigger.Event, reason, overwriteNextReview: false);

    private KycReviewSchedule Append(
        KycFile file, DateOnly due, KycReviewTrigger trigger, string? reason, bool overwriteNextReview)
    {
        var schedule = KycReviewSchedule.Schedule(file.TenantId, file.Id, due, trigger, clock, reason);
        db.KycReviewSchedules.Add(schedule);

        // NextReviewDate on the file is the cached head of the schedule table — every screen reads
        // it rather than querying the rows. An event-driven review only moves it EARLIER: an
        // alert raised today does not get to push a periodic deadline out by three years.
        if (overwriteNextReview || file.NextReviewDate is null || due < file.NextReviewDate)
            file.ScheduleNextReview(due, clock);

        return schedule;
    }

    /// <summary>The settings key carrying the periodicity for a vigilance level.</summary>
    private static string SettingKeyFor(KycVigilanceLevel level) => level switch
    {
        KycVigilanceLevel.High => KycSettingKeys.ReviewYearsHigh,
        KycVigilanceLevel.Low => KycSettingKeys.ReviewYearsLow,
        _ => KycSettingKeys.ReviewYearsStandard,
    };
}
