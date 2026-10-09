namespace Sankore.Modules.Integration.Tests.Features.Insurance;

using FluentAssertions;
using Sankore.Modules.Integration.Adapters.Fake;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.Features.Balance;
using Sankore.Modules.Integration.Tests.TestSupport;
using Xunit;

/// <summary>
/// <c>IInsuranceGateway</c>'s two reads over a tenant holding TWO active connections of one kind —
/// the defect in its most visible form.
///
/// <para>
/// <c>GetPoliciesAsync</c> and <c>GetClaimsAsync</c> iterate the tenant's active insurance
/// connections inside ONE scope, and ask each one's adapter whether it serves the read. The
/// adapter is resolved BY KIND, so both iterations get the same instance; until
/// <c>ICbsAdapter.CapabilitiesFor</c> took its connection, that instance could only answer one
/// matrix, discovered by re-reading "the tenant's connection" by kind — in practice the oldest
/// active row. Every iteration of the loop therefore read the FIRST row's capabilities, and a loop
/// over several connections that cannot tell them apart is not a loop.
/// </para>
///
/// <para>
/// <b>The arrangement puts the bug where it shows.</b> The older connection is fed by bordereaux
/// and declares no live reads — which is what <c>OrassCapabilityMatrix</c> really answers for a
/// file-fed insurer, since a one-way deposit answers no query; the newer one has an open API and
/// does. The adapter's DEFAULT matrix is the older one's, so an adapter that ignored its argument
/// would hand "no live reads" to both iterations and the gateway would return an EMPTY list for a
/// customer who holds a policy. The assertions below are exact rather than counts: a wrongly
/// INCLUDED connection fails them too, which is the symmetrical mistake.
/// </para>
///
/// <para>
/// <b>On the kind.</b> Both rows are <see cref="IntegrationKind.Fake"/>, because the point is two
/// rows resolving to ONE adapter instance and only a double can be told to answer differently per
/// row (<see cref="FakeAdapter.CapabilitiesByConnection"/>). The names carry the real story: ASS-01
/// places no single-active-connection limit on the insurance family, and the CIMA separation of
/// IARD and Vie undertakings makes two active rows for one insurer group the normal shape.
/// </para>
/// </summary>
public sealed class InsuranceGatewayPerConnectionTests : IDisposable
{
    /// <summary>
    /// The facade is built by <c>BalanceTestHarness</c>, so the tenant and the customer are its
    /// own: a second definition of "a facade over a fake adapter" would be free to drift from the
    /// one INT-15 is tested through, and this suite is about the gateway, not about the wiring.
    /// </summary>
    private static readonly Guid Tenant = BalanceTestHarness.TenantId;

    private static readonly Guid Customer = BalanceTestHarness.CrmCustomerId;

    /// <summary>
    /// The two creation instants. Distinct on purpose: the gateway orders by <c>CreatedAt</c>, and
    /// this test only means something if "the first row" is known.
    /// </summary>
    private static readonly DateTimeOffset IardCreatedAt = new(2026, 1, 10, 8, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset VieCreatedAt = new(2026, 2, 10, 8, 0, 0, TimeSpan.Zero);

    private const string IardCustomerRef = "ORASS-IARD-CUS-0001";
    private const string VieCustomerRef = "ORASS-VIE-CUS-0001";

    private const string IardPolicyRef = "POL-IARD-000001";
    private const string ViePolicyRef = "POL-VIE-000001";

    private readonly TestIntegrationDbContextFactory _factory = new(Tenant);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task The_policy_loop_asks_each_connection_about_itself()
    {
        await using var seed = _factory.CreateContext();

        var pair = SeedBothBranches(seed);

        await using var db = _factory.CreateContext();

        var adapter = AdapterServingLiveReadsFor(pair.Vie);

        var policies = await BalanceTestHarness.Facade(db, adapter).Insurance
            .GetPoliciesAsync(Customer, CancellationToken.None);

        // Exactly the one the API-fed insurer holds. Empty is what the single-matrix adapter
        // produced — the first row cannot read, so neither did the second — and two is what a
        // gateway that asked the wrong row the other way round would produce. Both are failures
        // of the same thing, and a count alone would not say which.
        policies.Should().ContainSingle()
            .Which.PolicyNumber.Should().Be(ViePolicyRef);

        // And the bordereau-fed insurer was never asked, which is the other half of the verdict: a
        // capability it does not declare is refused by IntegrationAdapterResolver.ResolvePort
        // BEFORE the port is reached, so there is no call to it at all. Not a stale answer, an
        // absent one — the policies of a file-fed insurer do not appear on a 360 screen until
        // ASS-07 projects them, and the adapter's own log is where that is observable.
        adapter.Calls.Where(c => c.Operation == FakeAdapterOperations.GetPolicies)
            .Select(c => c.Target)
            .Should().BeEquivalentTo([VieCustomerRef]);
    }

    [Fact]
    public async Task The_claim_loop_asks_each_connection_about_itself()
    {
        await using var seed = _factory.CreateContext();

        var pair = SeedBothBranches(seed);

        await using var db = _factory.CreateContext();

        var adapter = AdapterServingLiveReadsFor(pair.Vie);

        var claims = await BalanceTestHarness.Facade(db, adapter).Insurance
            .GetClaimsAsync(Customer, CancellationToken.None);

        // The claim loop is the nastier of the two: it reads the POLICY references of each
        // connection first, so it has rows to iterate for the bordereau-fed insurer as well, and
        // only the capability check stops it calling. Both insurers hold a declared claim here;
        // only the one that serves live reads may answer.
        claims.Should().ContainSingle()
            .Which.PolicyId.Value.Should().Be(ViePolicyRef);

        adapter.Calls.Where(c => c.Operation == FakeAdapterOperations.GetClaims)
            .Select(c => c.Target)
            .Should().BeEquivalentTo([ViePolicyRef]);
    }

    /// <summary>
    /// The two undertakings of one insurer group, both ACTIVE, each with a customer reference, a
    /// policy reference, a policy and a claim of its own — so that every row the loops iterate has
    /// something to return and the only thing separating the two is the matrix.
    /// </summary>
    private BothBranches SeedBothBranches(IntegrationDbContext seed)
    {
        var iard = InsuranceTestHarness.SeedConnection(
            seed, Tenant, IntegrationKind.Fake, name: "ORASS IARD", at: IardCreatedAt);

        var vie = InsuranceTestHarness.SeedConnection(
            seed, Tenant, IntegrationKind.Fake, name: "ORASS Vie", at: VieCreatedAt);

        SeedReference(seed, iard.Id, IntegrationEntityTypes.Customer, IardCustomerRef);
        SeedReference(seed, iard.Id, IntegrationEntityTypes.Policy, IardPolicyRef);

        SeedReference(seed, vie.Id, IntegrationEntityTypes.Customer, VieCustomerRef);
        SeedReference(seed, vie.Id, IntegrationEntityTypes.Policy, ViePolicyRef);

        seed.SaveChanges();

        return new BothBranches(iard, vie);
    }

    private static void SeedReference(
        IntegrationDbContext db, Guid connectionId, string entityType, string externalId)
        => db.References.Add(IntegrationReference.Create(
            tenantId: Tenant,
            connectionId: connectionId,
            kind: IntegrationKind.Fake,
            entityType: entityType,
            crmId: Customer,
            externalId: externalId,
            clock: InsuranceTestHarness.Clock()));

    /// <summary>
    /// The one adapter both rows resolve to. Its DEFAULT matrix is the bordereau-fed insurer's —
    /// the writes stay, the live reads are gone — and only <paramref name="liveReads"/> is
    /// overridden.
    ///
    /// <para>
    /// That direction is the whole point: the default is what an adapter answering from a single
    /// connection hands to every iteration of the gateway's loop, and the oldest active row is the
    /// one such an adapter bound itself to. Reverting <c>CapabilitiesFor</c> to answer from one
    /// connection therefore reproduces the production symptom here exactly — an empty policy list
    /// for a customer who holds a policy.
    /// </para>
    /// </summary>
    private static FakeAdapter AdapterServingLiveReadsFor(IntegrationConnection liveReads)
    {
        var everything = IntegrationCapabilities.All(
            CapabilityMode.RealTime, Enum.GetValues<IntegrationCapability>());

        var adapter = new FakeAdapter
        {
            Capabilities = new IntegrationCapabilities(
                everything.Modes
                    .Where(e => e.Key is not (IntegrationCapability.ReadPolicies
                                              or IntegrationCapability.ReadClaims))
                    .ToDictionary(e => e.Key, e => e.Value)),
        };

        adapter.CapabilitiesByConnection[liveReads.Id] = everything;

        // The double answers a read only for a customer it knows, so both insurers' references
        // have to be known to it: a refusal because the fake had never heard of the customer would
        // look exactly like a refusal by capability in the gateway's answer.
        adapter.Customers.Add(IardCustomerRef);
        adapter.Customers.Add(VieCustomerRef);

        adapter.PoliciesByCustomer[IardCustomerRef] = [Policy(IardPolicyRef)];
        adapter.PoliciesByCustomer[VieCustomerRef] = [Policy(ViePolicyRef)];

        adapter.ClaimsByPolicy[IardPolicyRef] = [Claim(IardPolicyRef)];
        adapter.ClaimsByPolicy[ViePolicyRef] = [Claim(ViePolicyRef)];

        return adapter;
    }

    /// <summary>
    /// One policy, named after its own reference so an assertion says which insurer answered.
    /// </summary>
    private static InsurancePolicy Policy(string reference)
        => new(
            new ExternalId(reference),
            PolicyNumber: reference,
            CrmCustomerId: Customer,
            InsurerProductCode: FakeAdapter.SeededInsurerProductCode,
            Status: PolicyStatus.Issued,
            EffectiveDate: new DateOnly(2026, 1, 1),
            ExpiryDate: new DateOnly(2026, 12, 31),
            PremiumAmount: 7_500m,
            Currency: "XOF",
            Periodicity: PremiumPeriodicity.Annual,
            NextDueDate: new DateOnly(2026, 2, 1));

    private static InsuranceClaim Claim(string policyReference)
        => new(
            new ExternalId($"CLM-{policyReference}"),
            ClaimNumber: $"SIN-{policyReference}",
            PolicyId: new ExternalId(policyReference),
            Status: ClaimStatus.UnderReview,
            OccurredOn: new DateOnly(2026, 3, 1),
            DeclaredAt: new DateTimeOffset(2026, 3, 2, 8, 0, 0, TimeSpan.Zero),
            IndemnityAmount: null,
            MissingDocuments: null);

    private sealed record BothBranches(IntegrationConnection Iard, IntegrationConnection Vie);
}
