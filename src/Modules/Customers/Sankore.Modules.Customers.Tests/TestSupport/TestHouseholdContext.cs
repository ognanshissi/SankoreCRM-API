namespace Sankore.Modules.Customers.Tests.TestSupport;

using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.Features.Holdings.Shared;
using Sankore.Modules.Customers.Features.Holdings.Sync;
using Sankore.Modules.Customers.Features.Household.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel.Authorization;

/// <summary>
/// Builds the two write contexts of BNF-B-13/14/15 over substituted modules.
///
/// <para>
/// REAL contexts and not substitutes: each one sequences the gates its zone's writes may not skip
/// (perimeter, PEP window, the policy read, the exposure recalculation), and a test that stubbed
/// them out would prove a handler calls something rather than proving the gate ran.
/// </para>
/// </summary>
internal static class TestHouseholdContext
{
    /// <summary>Compliance settings with FATCA off and a 12-month post-mandate window.</summary>
    public static ComplianceSettingsDto Compliance { get; } = new(
        FatcaEnabled: false, FatcaRegime: null, FatcaGiin: null,
        FatcaRequiredAt: "Conversion", FatcaForCoHolders: false,
        PepPostMandateMonths: 12);

    /// <summary>
    /// An Administration double answering the compliance settings. A caller supplying its own must
    /// stub <c>GetComplianceSettingsAsync</c>: the context FAILS CLOSED on an outage, and an
    /// unstubbed substitute returns a null Task — which surfaces as every write reporting
    /// COMPLIANCE_SETTINGS_UNAVAILABLE.
    /// </summary>
    public static IAdministrationModule Administration(Guid tenantId)
    {
        var admin = Substitute.For<IAdministrationModule>();
        admin.GetComplianceSettingsAsync(tenantId, Arg.Any<CancellationToken>())
             .Returns(Compliance);
        return admin;
    }

    internal static HouseholdWriteContext Household(
        CustomersDbContext db,
        Guid tenantId,
        Guid actorId,
        IEventPublisher publisher,
        IAdministrationModule? administration = null,
        IAgencyScopeProvider? agencyScope = null,
        TimeProvider? clock = null)
        => new(
            db,
            TestDoubles.CurrentUser(tenantId, actorId),
            agencyScope ?? TestDoubles.AgencyScope(),
            administration ?? Administration(tenantId),
            publisher,
            clock ?? TimeProvider.System);

    internal static HoldingWriteContext Holdings(
        CustomersDbContext db,
        Guid tenantId,
        Guid actorId,
        IAdministrationModule administration,
        IAgencyScopeProvider? agencyScope = null,
        TimeProvider? clock = null)
        => new(
            db,
            TestDoubles.CurrentUser(tenantId, actorId),
            agencyScope ?? TestDoubles.AgencyScope(),
            administration,
            clock ?? TimeProvider.System);
}

/// <summary>
/// A <see cref="HoldingSyncService"/> for the tests that need one (BNF-B-16).
/// </summary>
/// <remarks>
/// The facade takes one now, and three pre-existing test classes construct the facade for reasons
/// that have nothing to do with holdings. They get a real service over their own DbContext rather
/// than a null: the facade's other methods never touch it, and a null would turn an accidental
/// call into a <c>NullReferenceException</c> instead of a query that answers nothing.
/// </remarks>
internal static class TestHoldingSync
{
    internal static HoldingSyncService For(
        CustomersDbContext db,
        Guid tenantId,
        IAdministrationModule? administration = null)
        => new(
            db,
            administration ?? TestHouseholdContext.Administration(tenantId),
            new SubjectLabelResolver(db),
            TimeProvider.System);
}
