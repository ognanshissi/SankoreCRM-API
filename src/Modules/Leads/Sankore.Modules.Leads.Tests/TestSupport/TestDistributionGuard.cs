namespace Sankore.Modules.Leads.Tests.TestSupport;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Shared.Infrastructure.Compliance;
using Sankore.Shared.Kernel.Compliance;

/// <summary>
/// The CIMA professional-card guard, built for a test.
///
/// <para>
/// A real <c>InsuranceDistributionGuard</c> over a substituted provider, not a stub of the guard
/// itself: the rule under test is the guard's — which postures fail open and which fail closed —
/// and a double of it would let a handler be tested against behaviour the guard does not have.
/// </para>
/// </summary>
internal static class TestDistributionGuard
{
    /// <summary>
    /// No product line is an insurance line, so the guard never looks at a credential. The right
    /// default for the many suites that are about something else entirely: it reproduces what the
    /// overwhelming majority of dispatches and subscriptions actually do.
    /// </summary>
    internal static InsuranceDistributionGuard AllowAll() => Build(insurance: false, licensed: true);

    /// <summary>An insurance line whose holders DO hold a valid card.</summary>
    internal static InsuranceDistributionGuard InsuranceWithLicence()
        => Build(insurance: true, licensed: true);

    /// <summary>An insurance line whose holders hold NO card — the refusal path.</summary>
    internal static InsuranceDistributionGuard InsuranceWithoutLicence()
        => Build(insurance: true, licensed: false);

    internal static InsuranceDistributionGuard Build(bool insurance, bool licensed)
    {
        var provider = Substitute.For<IDistributionComplianceProvider>();

        provider.IsInsuranceProductLineAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(insurance);

        var snapshot = licensed
            ? new DistributionEligibilitySnapshot(
                DistributionEligibility.Valid, new DateOnly(2030, 1, 1), true, 48)
            : DistributionEligibilitySnapshot.NotRecorded;

        provider.GetDistributionEligibilityAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(snapshot);

        provider.GetDistributionEligibilitiesAsync(
                Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var ids = call.ArgAt<IReadOnlyCollection<Guid>>(1);
                return (IReadOnlyDictionary<Guid, DistributionEligibilitySnapshot>)
                    ids.ToDictionary(id => id, _ => snapshot);
            });

        return new InsuranceDistributionGuard(
            provider, TimeProvider.System, NullLogger<InsuranceDistributionGuard>.Instance);
    }
}
