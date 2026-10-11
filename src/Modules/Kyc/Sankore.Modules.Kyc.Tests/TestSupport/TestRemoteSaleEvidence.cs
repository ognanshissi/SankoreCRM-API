namespace Sankore.Modules.Kyc.Tests.TestSupport;

using NSubstitute;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Approval;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Infrastructure.Settings;

/// <summary>
/// The CIMA article 13.6 proof-of-address check, built for a test.
///
/// <para>
/// A REAL <c>RemoteSaleEvidence</c> over the test's own DbContext, not a stub: the rule is which
/// channels it applies to and which documents satisfy it, and a double would let the approval
/// handler be tested against behaviour the check does not have. Every suite about something else
/// gets the ordinary case — a file opened at a counter, where the requirement does not apply.
/// </para>
/// </summary>
internal static class TestRemoteSaleEvidence
{
    /// <param name="maxAgeDays">
    /// The window the settings answer with. The default matches the seeded 90 days.
    /// </param>
    internal static RemoteSaleEvidence For(
        KycDbContext db, TimeProvider clock, int maxAgeDays = 90)
    {
        var settings = Substitute.For<IKycSettings>();
        settings.GetIntAsync(
                Arg.Any<Guid>(), KycSettingKeys.ProofOfAddressMaxAgeDays, Arg.Any<CancellationToken>())
            .Returns(maxAgeDays);

        return new RemoteSaleEvidence(db, settings, clock);
    }
}

/// <summary>
/// The CIMA PEP funds-origin gate, built for a test.
///
/// <para>
/// A REAL <c>PepAuthorizationEvidence</c> over a substituted <c>ICustomersModule</c>: the rule is
/// that it applies only to a politically exposed file and that it fails CLOSED, and a double of
/// the check itself would let the approval handler be tested against behaviour it does not have.
/// The default answers "recorded", which is the ordinary case for every suite that is about
/// something else.
/// </para>
/// </summary>
internal static class TestPepEvidence
{
    internal static Sankore.Modules.Kyc.Features.Approval.PepAuthorizationEvidence
        For(bool fundsOriginRecorded = true)
    {
        var customers = Substitute.For<Sankore.Modules.Customers.PublicApi.ICustomersModule>();

        customers.HasRecordedFundsOriginAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(fundsOriginRecorded);

        return new Sankore.Modules.Kyc.Features.Approval.PepAuthorizationEvidence(
            customers,
            Microsoft.Extensions.Logging.Abstractions
                .NullLogger<Sankore.Modules.Kyc.Features.Approval.PepAuthorizationEvidence>
                .Instance);
    }
}
