namespace Sankore.Modules.Leads.Tests.Features.CustomerLink;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// Covers <c>ILeadsModule.MatchLeadsByEmailsAsync</c> (connected mailbox, lot 2): which leads a
/// page of a mailbox was exchanged with. A real-shaped indexer, for the reason
/// <see cref="FindOpenLeadsByPhoneTests"/> gives.
/// </summary>
public sealed class MatchLeadsByEmailsTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly ILeadEmailIndexer _indexer = new LowerCaseIndexer();

    public MatchLeadsByEmailsTests() => _factory = new TestDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Answers_each_known_address_with_its_leads_whatever_the_casing()
    {
        var aya = Capture("aya@example.ci");
        var koffi = Capture("koffi@example.ci");
        await SeedAsync(aya, koffi, Capture(null));

        var matches = await MatchAsync(" AYA@Example.ci ", "koffi@example.ci", "personne@example.ci");

        matches.Should().BeEquivalentTo(new[]
        {
            new PublicApi.LeadEmailMatch("aya@example.ci", aya.Id),
            new PublicApi.LeadEmailMatch("koffi@example.ci", koffi.Id),
        });
    }

    [Fact]
    public async Task A_closed_or_converted_lead_still_counts_but_an_anonymised_one_does_not()
    {
        // An exchange with a lost lead is still part of its history; an erased person is gone.
        var lost = Capture("lost@example.ci");
        lost.Close(LeadCloseReason.Lost, "pas de suite").IsSuccess.Should().BeTrue();
        var erased = Capture("erased@example.ci");
        erased.Anonymize(DateTimeOffset.UtcNow).Should().BeTrue();
        await SeedAsync(lost, erased);

        var matches = await MatchAsync("lost@example.ci", "erased@example.ci");

        matches.Should().ContainSingle().Which.LeadId.Should().Be(lost.Id);
    }

    [Fact]
    public async Task A_lead_the_backfill_has_not_reached_is_found_on_its_clear_column()
    {
        var legacy = Capture(null);
        await SeedAsync(legacy);
        await using (var write = _factory.CreateContext())
        {
            var stored = await write.Leads.AsTracking().SingleAsync();
            write.Entry(stored).Property(nameof(Lead.LegacyEmailClear)).CurrentValue = "Old@Example.ci";
            await write.SaveChangesAsync();
        }

        (await MatchAsync("old@example.ci")).Should().ContainSingle().Which.Email.Should().Be("old@example.ci");
    }

    [Fact]
    public async Task Another_tenants_leads_are_never_answered()
    {
        await SeedAsync(Capture("aya@example.ci"));

        await using var db = _factory.CreateContext();
        (await TestLeadsFacade.Build(db, emailIndexer: _indexer)
                .MatchLeadsByEmailsAsync(Guid.NewGuid(), ["aya@example.ci"], CancellationToken.None))
            .Should().BeEmpty();
    }

    private Lead Capture(string? email)
    {
        var lead = Lead.Capture(
            tenantId: _tenant,
            fullName: "Aya Kouassi",
            phoneNumber: "+2250700000001",
            source: LeadSource.Agency,
            interestedProduct: "Crédit commerçant",
            preferredLanguage: "fr",
            location: new GeoPoint(5.3, -4.0),
            preferredAgencyId: null,
            clock: TimeProvider.System);

        if (email is not null) lead.SetEmail(email, _indexer.Compute(email)).IsSuccess.Should().BeTrue();
        return lead;
    }

    private async Task SeedAsync(params Lead[] leads)
    {
        await using var db = _factory.CreateContext();
        db.Leads.AddRange(leads);
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<IReadOnlyList<PublicApi.LeadEmailMatch>> MatchAsync(params string[] emails)
    {
        await using var db = _factory.CreateContext();
        return await TestLeadsFacade.Build(db, emailIndexer: _indexer).MatchLeadsByEmailsAsync(_tenant, emails, CancellationToken.None);
    }

    private sealed class LowerCaseIndexer : ILeadEmailIndexer
    {
        public string? Compute(string? email) => string.IsNullOrWhiteSpace(email) ? null : "idx:" + email.Trim().ToLowerInvariant();
    }
}
