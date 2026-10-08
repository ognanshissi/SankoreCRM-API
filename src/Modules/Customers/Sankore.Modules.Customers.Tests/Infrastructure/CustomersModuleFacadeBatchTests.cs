namespace Sankore.Modules.Customers.Tests.Infrastructure;

using FluentAssertions;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// <c>GetClientSummariesAsync</c> — the batch read other modules use to put names on a page of
/// opaque client ids (M02's compliance worklist is the first caller).
///
/// What it has to get right is cheap to break and invisible when broken: the answer is keyed by the
/// id that was asked for, a stranger's id is absent rather than null-valued, and the tenant
/// predicate is applied even though the facade ignores the global query filters.
/// </summary>
public sealed class CustomersModuleFacadeBatchTests : IDisposable
{
    private static readonly Guid Agency = Guid.Parse("33333333-0000-0000-0000-000000000003");

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestCustomersDbContextFactory _factory;
    private readonly CustomersDbContext _db;
    private readonly ICustomersModule _facade;

    public CustomersModuleFacadeBatchTests()
    {
        _factory = new TestCustomersDbContextFactory(_tenantId);
        _db = _factory.CreateContext();
        _facade = new CustomersModuleFacade(_db, new UnusedLeadConversion());
    }

    public void Dispose()
    {
        _db.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Every_requested_id_comes_back_keyed_by_the_id_that_was_asked_for()
    {
        var awa = await TestClientFactory.SeedIndividualAsync(
            _db, _tenantId, Agency, first: "Awa", last: "Ouattara", clientNumber: "ABJ-2026-000001");
        var kone = await TestClientFactory.SeedIndividualAsync(
            _db, _tenantId, Agency, first: "Ibrahim", last: "Kone", clientNumber: "ABJ-2026-000002");

        var result = await _facade.GetClientSummariesAsync(_tenantId, [awa.Id, kone.Id], default);

        result.Should().HaveCount(2);
        result[awa.Id].DisplayName.Should().Be(awa.DisplayName);
        result[kone.Id].DisplayName.Should().Be(kone.DisplayName);
        result[awa.Id].Id.Should().Be(awa.Id, "the key and the payload must agree");
    }

    [Fact]
    public async Task An_unknown_id_is_absent_rather_than_present_with_nothing_in_it()
    {
        var known = await TestClientFactory.SeedIndividualAsync(_db, _tenantId, Agency);
        var dangling = Guid.NewGuid();

        var result = await _facade.GetClientSummariesAsync(_tenantId, [known.Id, dangling], default);

        result.Should().ContainKey(known.Id);
        result.Should().NotContainKey(dangling,
            "a caller resolving a page of references distinguishes 'unknown' by absence; a null "
            + "value in the dictionary would make every read a null check against a non-null type");
    }

    [Fact]
    public async Task A_client_of_another_tenant_is_not_returned_even_though_the_filters_are_ignored()
    {
        var mine = await TestClientFactory.SeedIndividualAsync(_db, _tenantId, Agency);

        // Written straight through a context bound to the other tenant, the way a second tenant's
        // rows genuinely sit in the same table.
        using var otherFactory = new TestCustomersDbContextFactory(Guid.NewGuid());
        using var otherDb = otherFactory.CreateContext();
        var theirs = await TestClientFactory.SeedIndividualAsync(
            otherDb, otherFactory.TenantId, Agency, clientNumber: "XXX-2026-000001");

        var result = await _facade.GetClientSummariesAsync(_tenantId, [mine.Id, theirs.Id], default);

        result.Should().ContainKey(mine.Id).And.NotContainKey(theirs.Id,
            "IgnoreQueryFilters() removes the only automatic guard, so the explicit predicate is "
            + "the whole protection — dropping it leaks names across tenants");
    }

    [Fact]
    public async Task A_duplicated_id_does_not_break_the_dictionary()
    {
        var client = await TestClientFactory.SeedIndividualAsync(_db, _tenantId, Agency);

        // Two KYC files on the same customer is ordinary, so a page can ask twice. Without the
        // Distinct() the ToDictionary throws on the duplicate key and the whole page 500s.
        var result = await _facade.GetClientSummariesAsync(
            _tenantId, [client.Id, client.Id], default);

        result.Should().ContainSingle().Which.Key.Should().Be(client.Id);
    }

    /// <summary>
    /// Hand-written rather than an NSubstitute double: <c>ILeadConversionService</c> is internal and
    /// the module assembly is not marked <c>InternalsVisibleTo("DynamicProxyGenAssembly2")</c>, so
    /// Castle cannot proxy it. Nothing under test reaches it.
    /// </summary>
    private sealed class UnusedLeadConversion : ILeadConversionService
    {
        public Task<Result<CreateFromLeadResult>> CreateFromLeadAsync(
            CreateFromLeadRequest request, CancellationToken ct)
            => throw new NotSupportedException("The batch read never converts a lead.");
    }

    [Fact]
    public async Task An_empty_batch_answers_empty_without_touching_the_database()
    {
        await TestClientFactory.SeedIndividualAsync(_db, _tenantId, Agency);

        var result = await _facade.GetClientSummariesAsync(_tenantId, [], default);

        result.Should().BeEmpty("an unfiltered list would be the dangerous way to answer this");
    }
}
