namespace Sankore.Modules.Customers.Tests.Features.Holdings;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Holdings.ListStalledHoldings;
using Sankore.Modules.Customers.Tests.Features.Compliance;
using Sankore.Modules.Customers.Tests.TestSupport;
using Xunit;

/// <summary>
/// The tenant-wide view of subscriptions that were queued for a back office and never came back
/// (BNF-B-16).
///
/// <para>
/// What it makes visible was invisible by construction. A subscription whose insurer cannot be
/// determined is refused inside Integration's consumer, which logs one warning and leaves the
/// holding in <c>PendingSync</c>; the reconciliation run cannot see it because a holding that was
/// never queued has no integration reference; and <c>ListHoldings</c> takes a client id, so
/// "which of my institution's subscriptions are stuck" had no form at all.
/// </para>
/// </summary>
public sealed class ListStalledHoldingsHandlerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _clientId = Guid.NewGuid();
    private readonly Guid _actor = Guid.NewGuid();
    private readonly TestCustomersDbContextFactory _factory;

    public ListStalledHoldingsHandlerTests() => _factory = new TestCustomersDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    /// <summary>
    /// A holding taken to <c>PendingSync</c> the way an agent would, then backdated so its age is
    /// known. <c>UpdatedAt</c> is what the handler measures, and the submission is the last thing
    /// that moves it in production.
    /// </summary>
    private async Task<Guid> SeedStalledAsync(int hoursAgo, Guid? clientId = null)
    {
        var line = TestHoldingFactory.Line();

        await using var db = _factory.CreateContext();
        var holding = TestHoldingFactory.Draft(
            _tenant, clientId ?? _clientId, line, _actor, Now);

        holding.Designate([TestHoldingFactory.Beneficiary(100m)], line.AllowedRoles, _actor, Now)
            .IsSuccess.Should().BeTrue();
        holding.RequestSignature([], Now).IsSuccess.Should().BeTrue();
        holding.Sign("doc://signature/1", Now, Now).IsSuccess.Should().BeTrue();
        holding.SubmitForSync(Now).IsSuccess.Should().BeTrue();

        db.ProductHoldings.Add(holding);
        await db.SaveChangesAsync();

        await using var backdate = _factory.CreateContext();
        var stored = await backdate.ProductHoldings.AsTracking().FirstAsync(h => h.Id == holding.Id);
        typeof(ProductHolding).GetProperty(nameof(ProductHolding.UpdatedAt))!
            .SetValue(stored, Now.AddHours(-hoursAgo));
        await backdate.SaveChangesAsync();

        return holding.Id;
    }

    /// <summary>A draft holding, which is not stuck — nobody is waiting on a back office.</summary>
    private async Task<Guid> SeedDraftAsync()
    {
        var line = TestHoldingFactory.Line();

        await using var db = _factory.CreateContext();
        var holding = TestHoldingFactory.Draft(_tenant, _clientId, line, _actor, Now.AddYears(-2));

        db.ProductHoldings.Add(holding);
        await db.SaveChangesAsync();
        return holding.Id;
    }

    private async Task<Sankore.Shared.Kernel.PagedResult<StalledHoldingDto>> ListAsync(
        int minAgeHours = 24, int page = 1, int pageSize = 50)
    {
        await using var db = _factory.CreateContext();

        var result = await new ListStalledHoldingsHandler(db, new FixedTimeProvider(Now))
            .Handle(new ListStalledHoldingsQuery(minAgeHours, page, pageSize), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        return result.Value!;
    }

    [Fact]
    public async Task A_holding_queued_longer_than_the_threshold_is_reported()
    {
        var id = await SeedStalledAsync(hoursAgo: 72);

        var page = await ListAsync(minAgeHours: 24);

        var row = page.Items.Should().ContainSingle().Subject;
        row.HoldingId.Should().Be(id);
        row.ClientId.Should().Be(_clientId);
        row.StalledSinceHours.Should().Be(72);
        row.SignedAt.Should().NotBeNull("a holding only reaches PendingSync once signed");
    }

    [Fact]
    public async Task A_holding_queued_moments_ago_is_not_reported()
    {
        // A few minutes in PendingSync is the healthy state of a queued command. Reporting it
        // would make every subscription in flight look like a fault.
        await SeedStalledAsync(hoursAgo: 1);

        (await ListAsync(minAgeHours: 24)).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task A_draft_is_never_stalled_however_old_it_is()
    {
        // Only PendingSync counts: nobody is waiting on a back office for a draft, however long
        // the agent has left it.
        await SeedDraftAsync();

        (await ListAsync(minAgeHours: 0)).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task The_threshold_is_the_caller_s_to_choose()
    {
        await SeedStalledAsync(hoursAgo: 6);

        (await ListAsync(minAgeHours: 24)).Items.Should().BeEmpty();
        (await ListAsync(minAgeHours: 4)).Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_negative_threshold_is_clamped_rather_than_refused()
    {
        // A caller asking for "anything at all" means zero, and a 400 for it would be pedantry
        // on an operations screen.
        await SeedStalledAsync(hoursAgo: 1);

        (await ListAsync(minAgeHours: -5)).Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task The_oldest_is_listed_first()
    {
        // The longest-stuck subscription is the one a client has been waiting on, and the
        // opposite order would bury it as the list grows.
        var newest = await SeedStalledAsync(hoursAgo: 30);
        var oldest = await SeedStalledAsync(hoursAgo: 400);
        var middle = await SeedStalledAsync(hoursAgo: 100);

        var page = await ListAsync();

        page.Items.Select(i => i.HoldingId).Should().ContainInOrder(oldest, middle, newest);
    }

    [Fact]
    public async Task Holdings_of_every_client_of_the_tenant_are_listed()
    {
        // The whole point of the view: it is not scoped to a client, because the question is
        // "what is stuck in my institution".
        await SeedStalledAsync(hoursAgo: 48);
        await SeedStalledAsync(hoursAgo: 48, clientId: Guid.NewGuid());

        (await ListAsync()).Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task Another_tenants_stalled_holding_is_not_listed()
    {
        await SeedStalledAsync(hoursAgo: 48);

        var otherTenant = Guid.NewGuid();
        await using (var other = _factory.ContextFor(otherTenant))
        {
            var line = TestHoldingFactory.Line();
            var holding = TestHoldingFactory.Draft(otherTenant, Guid.NewGuid(), line, _actor, Now);

            holding.Designate([TestHoldingFactory.Beneficiary(100m)], line.AllowedRoles, _actor, Now)
                .IsSuccess.Should().BeTrue();
            holding.RequestSignature([], Now).IsSuccess.Should().BeTrue();
            holding.Sign("doc://signature/2", Now, Now).IsSuccess.Should().BeTrue();
            holding.SubmitForSync(Now).IsSuccess.Should().BeTrue();

            other.ProductHoldings.Add(holding);
            await other.SaveChangesAsync();
        }

        (await ListAsync(minAgeHours: 0)).Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task The_row_carries_no_client_identity()
    {
        // Read by whoever fixes an integration mapping, which is a platform-level configuration.
        // It has no reason to carry the client's name, and the type is what guarantees it.
        await SeedStalledAsync(hoursAgo: 48);

        var row = (await ListAsync()).Items.Should().ContainSingle().Subject;

        typeof(StalledHoldingDto).GetProperties().Select(p => p.Name)
            .Should().BeEquivalentTo(
            [
                nameof(StalledHoldingDto.HoldingId),
                nameof(StalledHoldingDto.ClientId),
                nameof(StalledHoldingDto.ProductLineId),
                nameof(StalledHoldingDto.ProductCode),
                nameof(StalledHoldingDto.ProductCategory),
                nameof(StalledHoldingDto.SignedAt),
                nameof(StalledHoldingDto.UpdatedAt),
                nameof(StalledHoldingDto.StalledSinceHours),
            ]);

        row.Should().NotBeNull();
    }

    [Fact]
    public async Task The_total_counts_every_stalled_holding_not_only_the_page()
    {
        for (var i = 0; i < 5; i++)
            await SeedStalledAsync(hoursAgo: 48 + i);

        var page = await ListAsync(pageSize: 2);

        page.Items.Should().HaveCount(2);
        page.TotalCount.Should().Be(5);
    }
}
