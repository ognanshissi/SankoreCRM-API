namespace Sankore.Modules.Integration.Features.Snapshot;

using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Kyc.PublicApi;

/// <summary>
/// The one place M02's vocabulary becomes this module's, for criterion 4 of INT-21.
///
/// <para>
/// The two do not line up, which is why this is a method and not an inline comparison. M02 answers
/// with <see cref="KycLimits"/>, whose <c>Tier</c> is a STRING with two values and whose
/// <c>null</c> carries a third meaning ("no file at all, the customer may not operate"); this
/// module's <see cref="KycLevel"/> has three names, including <see cref="KycLevel.None"/>. Written
/// out once here, the way <c>KycStatusMapping</c> is in M02, so the judgement calls are visible
/// rather than repeated at each comparison:
/// </para>
///
/// <list type="bullet">
/// <item><c>null</c> → <see cref="KycLevel.None"/>. Fail-closed, and the same direction
///   <c>CbsCustomerPayloadSource</c> already takes when it pushes a tier out: "no file" must never
///   read as "unrestricted".</item>
/// <item><c>"Full"</c> → <see cref="KycLevel.Full"/>, anything else → <see cref="KycLevel.Simplified"/>.
///   M02 already collapses an expired full file back to the simplified ceilings before answering,
///   so reading its answer rather than its raw status keeps ONE definition of a tier in the
///   platform.</item>
/// </list>
/// </summary>
internal static class SnapshotKycLevels
{
    /// <summary>The tier M02 believes, in this module's vocabulary.</summary>
    internal static KycLevel FromLimits(KycLimits? limits)
    {
        if (limits is null) return KycLevel.None;

        return string.Equals(limits.Tier, nameof(KycLevel.Full), StringComparison.OrdinalIgnoreCase)
            ? KycLevel.Full
            : KycLevel.Simplified;
    }

    /// <summary>
    /// Whether the two sides disagree in a way worth reporting.
    ///
    /// <para>
    /// An UNKNOWN CBS tier is not a divergence. <c>cbsLevel</c> is null when nothing we have ever
    /// written to this CBS says what tier it holds, and publishing
    /// <c>CbsKycMismatchDetected</c> for that would report every customer created before the tier
    /// was ever pushed — a compliance queue full of rows whose only content is that we do not
    /// know. Silence is the honest answer, and the snapshot's own null column says it.
    /// </para>
    /// </summary>
    internal static bool Diverge(KycLevel crmLevel, KycLevel? cbsLevel)
        => cbsLevel is not null && cbsLevel.Value != crmLevel;
}
