namespace Sankore.Modules.Leads.Tests.TestSupport;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Enrichment;
using Sankore.Modules.Leads.Features.Fatca;
using Sankore.Modules.Leads.Infrastructure;

/// <summary>
/// Builds a real <see cref="LeadCompletenessCalculator"/> over substituted modules.
///
/// <para>
/// A REAL calculator and not a substitute, deliberately: it is the conversion gate, and a test
/// that stubs it out proves the handler calls something rather than proving the lead was complete.
/// Only the cross-module reads are doubled.
/// </para>
/// </summary>
internal static class TestCompleteness
{
    /// <summary>
    /// A calculator whose dependencies answer "nothing is configured": no product policy, FATCA
    /// off, no client. A lead whose asked product resolves to no line is then complete, which is
    /// what every pre-Lot-6 case in the suite assumes.
    /// </summary>
    /// <remarks>
    /// A caller supplying its OWN <see cref="IAdministrationModule"/> must stub
    /// <c>GetComplianceSettingsAsync</c> with <see cref="FatcaOff"/> or its own value:
    /// <c>FatcaSubjectPlanner</c> fails closed, and an unstubbed substitute returns a null Task,
    /// which surfaces as every lead reporting <c>COMPLETENESS_CHECK_UNAVAILABLE</c>.
    /// </remarks>
    internal static LeadCompletenessCalculator Calculator(
        LeadsDbContext db,
        IAdministrationModule? administration = null,
        ICustomersModule? customers = null,
        IKycModule? kyc = null)
    {
        var admin = administration ?? Substitute.For<IAdministrationModule>();

        if (administration is null)
        {
            // FATCA off by default. The planner FAILS on an outage, and a substitute returning
            // null for a non-nullable DTO would make every lead report
            // COMPLETENESS_CHECK_UNAVAILABLE — a failure that reads as a bug in the handler.
            admin.GetComplianceSettingsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                 .Returns(FatcaOff);
        }

        return new LeadCompletenessCalculator(
            db,
            admin,
            customers ?? Substitute.For<ICustomersModule>(),
            kyc ?? Substitute.For<IKycModule>(),
            new FatcaSubjectPlanner(admin, NullLogger<FatcaSubjectPlanner>.Instance));
    }

    /// <summary>Compliance settings with FATCA off — the platform default.</summary>
    internal static ComplianceSettingsDto FatcaOff { get; } = new(
        FatcaEnabled: false, FatcaRegime: null, FatcaGiin: null,
        FatcaRequiredAt: "Conversion", FatcaForCoHolders: false,
        PepEnabled: true,
        PepPostMandateMonths: 12);

    /// <summary>
    /// The same, with PEP vigilance switched off — what a tenant that has used the toggle looks
    /// like to this calculator.
    /// </summary>
    internal static ComplianceSettingsDto PepOff { get; } = FatcaOff with { PepEnabled = false };
}
