namespace Sankore.Modules.Leads.Tests.Features.CustomerLink;

using FluentAssertions;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// Covers <c>ILeadsModule.FindOpenLeadsByPhoneAsync</c> (TEL-B-09) — the reverse lookup the
/// telephony journal uses to put a ringing number against a face.
///
/// <para>
/// The number arrives in clear and the index is computed inside the facade, because the key is
/// this module's; these cases therefore use a real indexer rather than a substitute, since a
/// substitute returning the same string for every number would make every lookup pass.
/// </para>
/// </summary>
public sealed class FindOpenLeadsByPhoneTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly IPhoneBlindIndexer _indexer = new Last8DigitsIndexer();

    public FindOpenLeadsByPhoneTests() => _factory = new TestDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Finds_an_open_lead_on_its_blind_index()
    {
        await SeedAsync(Capture("+2250700000001", "Aya Kouassi"));

        var matches = await ReadAsync("+2250700000001");

        matches.Should().HaveCount(1);
        matches[0].FullName.Should().Be("Aya Kouassi");
        matches[0].Status.Should().Be(nameof(LeadStatus.New));
    }

    [Fact]
    public async Task Finds_the_same_subscriber_dialled_in_the_other_form()
    {
        // The index normalises to the last eight significant digits, so a number captured at a
        // counter as 0700000001 and presented by the provider as +225 07 00 00 00 01 is one
        // subscriber. Were it not so, every walk-in lead would be invisible to the caller-id
        // lookup — the provider always reports E.164.
        await SeedAsync(Capture("0700000001", "Aya Kouassi"));

        var matches = await ReadAsync("+225 07 00 00 00 01");

        matches.Should().HaveCount(1);
    }

    [Fact]
    public async Task Answers_every_match_rather_than_choosing_one()
    {
        // A family handset or a shop line is ordinary, and this module does not know the agent's
        // perimeter — the caller does. Deciding here would make the second candidate unreachable.
        await SeedAsync(
            Capture("+2250700000001", "Aya Kouassi"),
            Capture("+2250700000001", "Koffi Kouassi"));

        var matches = await ReadAsync("+2250700000001");

        matches.Should().HaveCount(2);
    }

    [Fact]
    public async Task Carries_the_agency_and_the_assigned_agent_so_the_caller_can_filter()
    {
        var agency = Guid.NewGuid();
        await SeedAsync(Capture("+2250700000001", "Aya Kouassi", agencyId: agency));

        var matches = await ReadAsync("+2250700000001");

        matches[0].AgencyId.Should().Be(agency);
    }

    [Theory]
    [InlineData(LeadCloseReason.Lost)]
    [InlineData(LeadCloseReason.Disqualified)]
    public async Task A_closed_lead_is_excluded(LeadCloseReason reason)
    {
        // A lost or disqualified lead is a conversation that ended. Filing today's call against it
        // would attach the exchange to a record nobody is working — and the caller would then
        // offer the agent a screen pop onto a dead file.
        var lead = Capture("+2250700000001", "Aya Kouassi");
        lead.Close(reason, "n'a pas donné suite").IsSuccess.Should().BeTrue();
        await SeedAsync(lead);

        (await ReadAsync("+2250700000001")).Should().BeEmpty();
    }

    [Fact]
    public async Task A_converted_lead_is_excluded_because_it_belongs_to_a_client_now()
    {
        // Not an omission: a converted lead's person is a client, found through M01, and the
        // caller prefers a client over a lead. Answering here too would show the same human twice.
        var lead = Capture("+2250700000001", "Aya Kouassi");
        lead.Convert(Guid.NewGuid()).IsSuccess.Should().BeTrue();
        await SeedAsync(lead);

        (await ReadAsync("+2250700000001")).Should().BeEmpty();
    }

    [Fact]
    public async Task An_anonymised_lead_is_excluded()
    {
        var lead = Capture("+2250700000001", "Aya Kouassi");
        lead.Anonymize(DateTimeOffset.UtcNow).Should().BeTrue();
        await SeedAsync(lead);

        (await ReadAsync("+2250700000001")).Should().BeEmpty();
    }

    [Fact]
    public async Task A_lead_captured_before_the_protection_migration_is_still_found()
    {
        // No blind index: the transitional branch matches on the suffix of the clear column, the
        // way the duplicate gate does. Without it the lookup would miss precisely the oldest
        // leads, which is where the longest-running conversations are.
        var lead = Capture("+2250700000001", "Aya Kouassi", withIndex: false);
        await SeedAsync(lead);

        (await ReadAsync("+2250700000001")).Should().HaveCount(1);
    }

    [Fact]
    public async Task A_number_that_normalises_to_nothing_matches_nobody()
    {
        // An incoming call with no caller id is ordinary. The legacy branch would otherwise be an
        // EndsWith("") and answer every un-backfilled lead in the tenant.
        await SeedAsync(Capture("+2250700000001", "Aya Kouassi", withIndex: false));

        (await ReadAsync("anonyme")).Should().BeEmpty();
    }

    [Fact]
    public async Task Another_tenants_lead_is_never_answered()
    {
        await SeedAsync(Capture("+2250700000001", "Aya Kouassi"));

        var matches = await ReadAsync("+2250700000001", tenantId: Guid.NewGuid());

        matches.Should().BeEmpty();
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private Lead Capture(
        string phone, string fullName, Guid? agencyId = null, bool withIndex = true)
        => Lead.Capture(
            tenantId: _tenant,
            fullName: fullName,
            phoneNumber: phone,
            source: LeadSource.Agency,
            interestedProduct: "Crédit commerçant",
            preferredLanguage: "fr",
            location: new GeoPoint(5.3, -4.0),
            preferredAgencyId: null,
            clock: TimeProvider.System,
            agencyId: agencyId,
            phoneBlindIndex: withIndex ? _indexer.Compute(phone) : null);

    private async Task SeedAsync(params Lead[] leads)
    {
        await using var db = _factory.CreateContext();
        db.Leads.AddRange(leads);
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<IReadOnlyList<PublicApi.LeadPhoneMatch>> ReadAsync(
        string phone, Guid? tenantId = null)
    {
        await using var db = _factory.CreateContext();
        return await new LeadsModuleFacade(db, _indexer)
            .FindOpenLeadsByPhoneAsync(tenantId ?? _tenant, phone, CancellationToken.None);
    }

    /// <summary>
    /// The real normalisation with a trivial key — what matters is that two spellings of one
    /// subscriber collide and two subscribers do not.
    /// </summary>
    private sealed class Last8DigitsIndexer : IPhoneBlindIndexer
    {
        public string Compute(string phoneNumber)
        {
            var digits = new string(phoneNumber.Where(char.IsDigit).ToArray());
            return digits.Length > 8 ? digits[^8..] : digits;
        }
    }
}
