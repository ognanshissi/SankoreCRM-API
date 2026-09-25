namespace Sankore.Modules.Leads.Tests.Features.Concurrency;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.CloseLead;
using Sankore.Modules.Leads.Tests.TestSupport;
using Xunit;

/// <summary>
/// Covers the opt-in staleness guard on client-driven lead edits
/// (<see cref="LeadConcurrency"/>), using CloseLead as the representative slice.
/// </summary>
public sealed class LeadConcurrencyTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;

    public LeadConcurrencyTests() => _factory = new TestDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    // ── Domain helper ───────────────────────────────────────────────────

    [Fact]
    public void A_null_expectation_opts_out_of_the_check()
    {
        var lead = LeadTestBuilder.Create().WithTenant(_tenantId).Build();
        lead.IsStale(null).Should().BeFalse();
    }

    [Fact]
    public void An_expectation_truncated_to_milliseconds_is_not_stale()
    {
        var lead = LeadTestBuilder.Create().WithTenant(_tenantId).Build();

        // What a JavaScript Date round-trip does to the microseconds Postgres stores.
        var asJavaScriptWouldSendIt =
            DateTimeOffset.FromUnixTimeMilliseconds(lead.UpdatedAt.ToUnixTimeMilliseconds());

        lead.IsStale(asJavaScriptWouldSendIt).Should().BeFalse();
    }

    [Fact]
    public void An_older_expectation_is_stale()
    {
        var lead = LeadTestBuilder.Create().WithTenant(_tenantId).Build();
        lead.IsStale(lead.UpdatedAt.AddSeconds(-1)).Should().BeTrue();
    }

    // ── Handler behaviour ───────────────────────────────────────────────

    [Fact]
    public async Task Close_with_a_stale_expectation_returns_CONFLICT_and_leaves_the_lead_open()
    {
        var lead = await SeedLeadAsync();

        await using var db = _factory.CreateContext();
        var result = await new CloseLeadHandler(db).Handle(
            new CloseLeadCommand(lead.Id, LeadCloseReason.Lost, "stale editor",
                ExpectedUpdatedAt: lead.UpdatedAt.AddMinutes(-5)),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("CONFLICT");

        var reloaded = await db.Leads.IgnoreQueryFilters().SingleAsync(l => l.Id == lead.Id);
        reloaded.Status.Should().NotBe(LeadStatus.Lost);
    }

    [Fact]
    public async Task Close_with_the_current_expectation_succeeds()
    {
        var lead = await SeedLeadAsync();

        await using var db = _factory.CreateContext();
        var result = await new CloseLeadHandler(db).Handle(
            new CloseLeadCommand(lead.Id, LeadCloseReason.Lost, "no answer",
                ExpectedUpdatedAt: lead.UpdatedAt),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        var reloaded = await db.Leads.IgnoreQueryFilters().SingleAsync(l => l.Id == lead.Id);
        reloaded.Status.Should().Be(LeadStatus.Lost);
    }

    [Fact]
    public async Task Close_without_an_expectation_still_succeeds_for_older_clients()
    {
        var lead = await SeedLeadAsync();

        await using var db = _factory.CreateContext();
        var result = await new CloseLeadHandler(db).Handle(
            new CloseLeadCommand(lead.Id, LeadCloseReason.Lost, "no answer"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    private async Task<Lead> SeedLeadAsync()
    {
        var lead = LeadTestBuilder.Create().WithTenant(_tenantId).Build();

        await using var db = _factory.CreateContext();
        db.Leads.Add(lead);
        await db.SaveChangesAsync();

        return lead;
    }
}
