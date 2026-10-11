namespace Sankore.Modules.Customers.Tests.Features.Compliance;

using FluentAssertions;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.Tests.TestSupport;
using Xunit;

/// <summary>
/// Which clients owe an update today, measured from their contract's own date.
///
/// <para>
/// A date-driven lookup and NOT an enumeration of holdings: <c>ICustomersModule</c> deliberately
/// exposes no way to walk a tenant's portfolio, and the reconciliation run's own notes explain
/// why. What this answers is a few rows a night.
/// </para>
/// </summary>
public sealed class ContractAnniversaryLookupTests : IDisposable
{
    private static readonly DateTimeOffset Signed = new(2024, 3, 15, 10, 0, 0, TimeSpan.Zero);

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _actor = Guid.NewGuid();
    private readonly TestCustomersDbContextFactory _factory;

    public ContractAnniversaryLookupTests()
        => _factory = new TestCustomersDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    private async Task<Guid> SeedHoldingAsync(
        DateOnly? effectiveFrom,
        DateTimeOffset? signedAt,
        HoldingStatus status = HoldingStatus.Active)
    {
        var clientId = Guid.NewGuid();
        var line = TestHoldingFactory.Line();

        await using var db = _factory.CreateContext();
        var holding = TestHoldingFactory.Draft(_tenant, clientId, line, _actor, Signed);

        if (effectiveFrom is not null)
        {
            holding.UpdateSpecifics(
                HoldingSpecifics.None with { EffectiveFrom = effectiveFrom }, Signed)
                .IsSuccess.Should().BeTrue();
        }

        // The state machine is pinned elsewhere; what this suite is about is the DATE, so the
        // status and the signature are set directly rather than walked to.
        typeof(ProductHolding).GetProperty(nameof(ProductHolding.Status))!
            .SetValue(holding, status);
        typeof(ProductHolding).GetProperty(nameof(ProductHolding.SignedAt))!
            .SetValue(holding, signedAt);

        db.ProductHoldings.Add(holding);
        await db.SaveChangesAsync();
        return clientId;
    }

    /// <summary>
    /// The facade over this suite's context, with its other dependencies left NULL.
    ///
    /// <para>
    /// Deliberate, and safe for exactly one reason: the method under test reads
    /// <c>product_holdings</c> and touches none of them. Substituting them is not an option —
    /// <c>ILeadConversionService</c> is internal to the module and Castle cannot proxy it — and
    /// nulls say plainly which dependencies this test does not exercise. A method that later
    /// started using one would NullReference here rather than pass quietly.
    /// </para>
    /// </summary>
    private static CustomersModuleFacade Facade(CustomersDbContext db) => new(db, null!, null!, null!);

    private async Task<IReadOnlyList<Guid>> OnAsync(DateOnly day)
    {
        await using var db = _factory.CreateContext();

        return await Facade(db)
            .GetClientsWithContractAnniversaryAsync(_tenant, day, CancellationToken.None);
    }

    [Fact]
    public async Task The_anniversary_is_the_effective_date_when_there_is_one()
    {
        var clientId = await SeedHoldingAsync(
            effectiveFrom: new DateOnly(2024, 3, 15), signedAt: Signed);

        (await OnAsync(new DateOnly(2026, 3, 15))).Should().Equal(clientId);
        (await OnAsync(new DateOnly(2026, 3, 16))).Should().BeEmpty();
    }

    [Fact]
    public async Task It_falls_back_to_the_signature_when_no_effective_date_was_recorded()
    {
        // A contract that never recorded an effective date still began on the day the client
        // signed it.
        var clientId = await SeedHoldingAsync(effectiveFrom: null, signedAt: Signed);

        (await OnAsync(new DateOnly(2026, 3, 15))).Should().Equal(clientId);
    }

    [Fact]
    public async Task A_holding_with_neither_date_has_no_anniversary()
    {
        // Skipped rather than given an arbitrary one: a review raised on a date nobody agreed to
        // is noise a compliance officer learns to ignore.
        await SeedHoldingAsync(effectiveFrom: null, signedAt: null);

        (await OnAsync(new DateOnly(2026, 3, 15))).Should().BeEmpty();
    }

    [Fact]
    public async Task The_year_the_contract_began_is_not_an_anniversary_of_itself()
    {
        // A file brought up to date on the day it was opened is the opening, not an update.
        await SeedHoldingAsync(effectiveFrom: new DateOnly(2026, 3, 15), signedAt: Signed);

        (await OnAsync(new DateOnly(2026, 3, 15))).Should().BeEmpty();
    }

    [Fact]
    public async Task A_leap_day_contract_is_reviewed_every_year_and_not_once_in_four()
    {
        var clientId = await SeedHoldingAsync(
            effectiveFrom: new DateOnly(2024, 2, 29), signedAt: Signed);

        // 2026 is not a leap year, so the anniversary folds onto 28 February.
        (await OnAsync(new DateOnly(2026, 2, 28))).Should().Equal(clientId);

        // And in a leap year it is the day itself.
        (await OnAsync(new DateOnly(2028, 2, 29))).Should().Equal(clientId);
    }

    [Theory]
    [InlineData(HoldingStatus.Closed)]
    [InlineData(HoldingStatus.Rejected)]
    public async Task A_dead_contract_has_no_anniversary_to_honour(HoldingStatus status)
    {
        await SeedHoldingAsync(
            effectiveFrom: new DateOnly(2024, 3, 15), signedAt: Signed, status: status);

        (await OnAsync(new DateOnly(2026, 3, 15))).Should().BeEmpty();
    }

    [Fact]
    public async Task A_client_holding_two_contracts_with_the_same_anniversary_appears_once()
    {
        // Two reviews for one client on one night would be two tasks for one conversation.
        var clientId = Guid.NewGuid();
        var line = TestHoldingFactory.Line();

        await using (var db = _factory.CreateContext())
        {
            foreach (var _ in Enumerable.Range(0, 2))
            {
                var holding = TestHoldingFactory.Draft(_tenant, clientId, line, _actor, Signed);

                holding.UpdateSpecifics(
                    HoldingSpecifics.None with { EffectiveFrom = new DateOnly(2024, 3, 15) },
                    Signed).IsSuccess.Should().BeTrue();

                typeof(ProductHolding).GetProperty(nameof(ProductHolding.Status))!
                    .SetValue(holding, HoldingStatus.Active);

                db.ProductHoldings.Add(holding);
            }

            await db.SaveChangesAsync();
        }

        (await OnAsync(new DateOnly(2026, 3, 15))).Should().Equal(clientId);
    }

    [Fact]
    public async Task Another_tenants_contract_is_not_reported()
    {
        await SeedHoldingAsync(effectiveFrom: new DateOnly(2024, 3, 15), signedAt: Signed);

        await using var db = _factory.CreateContext();
        var answer = await Facade(db)
            .GetClientsWithContractAnniversaryAsync(
                Guid.NewGuid(), new DateOnly(2026, 3, 15), CancellationToken.None);

        answer.Should().BeEmpty();
    }
}
