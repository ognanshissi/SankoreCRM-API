namespace Sankore.Modules.Kyc.Tests.Domain;

using FluentAssertions;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.PublicApi;
using Xunit;

/// <summary>
/// The public contract predates this module and M01 already consumes it — its retention job and
/// its anonymisation handler both call IKycModule. The ten internal statuses must therefore fold
/// onto the contract's six, and the two judgement calls in that projection are pinned here rather
/// than left to be rediscovered from a switch statement.
/// </summary>
public sealed class KycStatusMappingTests
{
    [Theory]
    [InlineData(KycFileStatus.Collecting, KycStatus.Pending)]
    [InlineData(KycFileStatus.Verifying, KycStatus.InProgress)]
    [InlineData(KycFileStatus.Validating, KycStatus.InProgress)]
    [InlineData(KycFileStatus.ComplementRequired, KycStatus.InProgress)]
    [InlineData(KycFileStatus.Simplified, KycStatus.Approved)]
    [InlineData(KycFileStatus.Full, KycStatus.Approved)]
    [InlineData(KycFileStatus.UnderReview, KycStatus.Approved)]
    [InlineData(KycFileStatus.Expired, KycStatus.Expired)]
    [InlineData(KycFileStatus.Rejected, KycStatus.Rejected)]
    [InlineData(KycFileStatus.Suspended, KycStatus.Rejected)]
    public void Each_internal_status_projects_onto_the_contract(
        KycFileStatus internalStatus, KycStatus expected)
        => internalStatus.ToPublicStatus().Should().Be(expected);

    [Fact]
    public void A_review_in_progress_keeps_the_customer_approved()
    {
        // Reporting UnderReview as pending would freeze operations for a client in good standing,
        // every time a periodic review comes round.
        KycFileStatus.UnderReview.ToPublicStatus().Should().Be(KycStatus.Approved);
    }

    [Fact]
    public void A_compliance_block_reads_exactly_like_a_refusal()
    {
        // Fail-closed: a downstream gate must never treat a suspension as "still being processed".
        KycFileStatus.Suspended.ToPublicStatus().Should().Be(KycStatus.Rejected);
        KycFileStatus.Suspended.ToPublicStatus().Should().NotBe(KycStatus.InProgress);
    }

    [Fact]
    public void Every_internal_status_is_mapped()
    {
        // Guards the day someone adds an eleventh status: the switch falls through to NotStarted,
        // which would silently report a real file as never having begun.
        foreach (var status in Enum.GetValues<KycFileStatus>())
            status.ToPublicStatus().Should().NotBe(KycStatus.NotStarted, $"{status} must be mapped");
    }

    [Fact]
    public void NotStarted_stays_reserved_for_the_absence_of_a_file()
    {
        // StubKycModule returns NotStarted when the module is not deployed. No real status may
        // collide with that meaning.
        Enum.GetValues<KycFileStatus>()
            .Select(s => s.ToPublicStatus())
            .Should().NotContain(KycStatus.NotStarted);
    }
}
