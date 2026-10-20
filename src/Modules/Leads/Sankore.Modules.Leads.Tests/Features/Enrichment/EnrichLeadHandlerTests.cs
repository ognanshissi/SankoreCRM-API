namespace Sankore.Modules.Leads.Tests.Features.Enrichment;

using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Enrichment.EnrichLead;
using Sankore.Modules.Leads.Features.Enrichment.Events;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Modules.Leads.Features.Questionnaires;
using Sankore.Modules.Leads.Features.RecalculateLeadScore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// Enrichment after dispatching (BNF-B-02): the principal's identity completed field by field, and
/// the product line resolved by somebody who checked the catalogue.
/// </summary>
public sealed class EnrichLeadHandlerTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _agent = Guid.NewGuid();
    private readonly Guid _line = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();
    private readonly IEventPublisher _publisher = Substitute.For<IEventPublisher>();
    private readonly ISender _sender = Substitute.For<ISender>();
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    public EnrichLeadHandlerTests()
    {
        _factory = new TestDbContextFactory(_tenant);
        _admin.GetComplianceSettingsAsync(_tenant, Arg.Any<CancellationToken>())
              .Returns(TestCompleteness.FatcaOff);
        _admin.GetBeneficiaryPolicyAsync(_tenant, _line, Arg.Any<CancellationToken>())
              .Returns(new BeneficiaryPolicyDto(
                  _line, "SANTE-01", nameof(ProductCategory.HealthInsurance),
                  true, false, [], null, null, [], null, null));
    }

    public void Dispose() => _factory.Dispose();

    // ── fixtures ────────────────────────────────────────────────────────────

    private async Task<Lead> SeedAsync(bool withNames = false)
    {
        var lead = Lead.Capture(
            tenantId: _tenant, fullName: "Aya Kouassi", phoneNumber: "+2250700000001",
            source: LeadSource.Agency, interestedProduct: "Assurance santé",
            preferredLanguage: "fr", location: new GeoPoint(5.3, -4.0),
            preferredAgencyId: null, clock: TimeProvider.System,
            firstName: withNames ? "Aya" : null,
            lastName: withNames ? "Kouassi" : null,
            ownerId: _agent);

        await using var db = _factory.CreateContext();
        db.Leads.Add(lead);
        await db.SaveChangesAsync(CancellationToken.None);
        return lead;
    }

    private EnrichLeadHandler Handler(LeadsDbContext db)
    {
        var user = Substitute.For<ICurrentUser>();
        user.Id.Returns(_agent);
        user.TenantId.Returns(_tenant);
        user.Roles.Returns([Roles.CommercialAgent.Code]);

        var context = new HouseholdWriteContext(
            db,
            new LeadAccessGuard(user, _admin),
            new HouseholdPolicyResolver(_admin),
            _admin,
            new ExpectedResponsePlanner(db, _admin, TimeProvider.System),
            _publisher,
            new FixedClock(Now),
            NullLogger<HouseholdWriteContext>.Instance);

        return new EnrichLeadHandler(
            context, db, _admin, TestCompleteness.Calculator(db, _admin), user, _sender,
            Options.Create(new LeadModuleSettings()), _publisher);
    }

    private async Task<Result<Sankore.Modules.Leads.Features.Enrichment.LeadCompletenessDto>>
        SendAsync(EnrichLeadCommand cmd)
    {
        await using var db = _factory.CreateContext();
        return await Handler(db).Handle(cmd, CancellationToken.None);
    }

    private async Task<Lead> ReloadAsync(Guid leadId)
    {
        await using var db = _factory.CreateContext();
        return await db.Leads.Include(l => l.HouseholdMembers)
                             .FirstAsync(l => l.Id == leadId, CancellationToken.None);
    }

    // ── how the identity screen finds the house, and what it vouches for ───

    [Fact]
    public async Task It_stores_the_neighbourhood_and_the_landmark_next_to_the_postal_fields()
    {
        var lead = await SeedAsync(withNames: true);

        var result = await SendAsync(new EnrichLeadCommand(
            lead.Id, City: "Abidjan", Neighbourhood: " Cocody Angré ", Landmark: "Derrière la pharmacie du marché"));

        result.IsSuccess.Should().BeTrue(result.Error);
        var stored = await ReloadAsync(lead.Id);
        stored.PrincipalNeighbourhood.Should().Be("Cocody Angré");
        stored.PrincipalLandmark.Should().Be("Derrière la pharmacie du marché");

        // Blank clears, null leaves alone — the rule every other field of the command follows.
        await SendAsync(new EnrichLeadCommand(lead.Id, Landmark: ""));
        stored = await ReloadAsync(lead.Id);
        stored.PrincipalLandmark.Should().BeNull();
        stored.PrincipalNeighbourhood.Should().Be("Cocody Angré");
    }

    [Fact]
    public async Task Confirming_the_name_records_who_did_it_and_when()
    {
        var lead = await SeedAsync(withNames: true);

        var result = await SendAsync(new EnrichLeadCommand(lead.Id, NameVerified: true));

        result.IsSuccess.Should().BeTrue(result.Error);
        var stored = await ReloadAsync(lead.Id);
        stored.NameVerifiedAt.Should().Be(Now);
        stored.NameVerifiedBy.Should().Be(_agent);
    }

    [Fact]
    public async Task The_call_that_confirmed_the_identity_is_kept_on_the_lead()
    {
        var lead = await SeedAsync(withNames: true);
        Guid activityId;
        await using (var db = _factory.CreateContext())
        {
            var call = LeadActivity.Create(
                tenantId: _tenant, leadId: lead.Id, type: ActivityType.Call, subject: "Appel",
                performedBy: _agent);
            db.LeadActivities.Add(call);
            await db.SaveChangesAsync(CancellationToken.None);
            activityId = call.Id;
        }

        var result = await SendAsync(new EnrichLeadCommand(lead.Id, SourceActivityId: activityId));

        result.IsSuccess.Should().BeTrue(result.Error);
        (await ReloadAsync(lead.Id)).IdentitySourceActivityId.Should().Be(activityId);
    }

    [Fact]
    public async Task Saving_the_address_rescores_the_lead_and_confirming_the_name_does_not()
    {
        // The screen promises the agent the address is worth points: the score has to move on the
        // save, not at the next logged activity.
        var lead = await SeedAsync(withNames: true);

        await SendAsync(new EnrichLeadCommand(lead.Id, Neighbourhood: "Cocody"));
        await _sender.Received(1).Send(
            Arg.Is<RecalculateLeadScoreCommand>(c => c.LeadId == lead.Id && c.TriggerEvent == "LEAD_ENRICHED"),
            Arg.Any<CancellationToken>());

        _sender.ClearReceivedCalls();
        await SendAsync(new EnrichLeadCommand(lead.Id, NameVerified: true));
        await _sender.DidNotReceive().Send(Arg.Any<RecalculateLeadScoreCommand>(), Arg.Any<CancellationToken>());
    }

    // ── the point of the feature ────────────────────────────────────────────

    [Fact]
    public async Task It_writes_the_principals_identity_and_postal_address()
    {
        var lead = await SeedAsync();

        var result = await SendAsync(new EnrichLeadCommand(
            LeadId:      lead.Id,
            FirstName:   "Aya",
            LastName:    "Kouassi",
            UsageName:   "Aya K.",
            BirthDate:   new DateOnly(1990, 4, 2),
            Gender:      nameof(LeadGender.Female),
            AddressLine: "12 rue des Jardins",
            PostalCode:  "01 BP 1234",
            City:        "Abidjan",
            CountryCode: "ci"));

        result.IsSuccess.Should().BeTrue(result.Error);

        var stored = await ReloadAsync(lead.Id);
        stored.PrincipalIdentity.Should().NotBeNull();
        stored.PrincipalIdentity!.UsageName.Should().Be("Aya K.");
        stored.PrincipalIdentity.AddressLine.Should().Be("12 rue des Jardins");
        stored.PrincipalIdentity.City.Should().Be("Abidjan");
        stored.PrincipalCountryCode.Should().Be("CI", "ISO codes are stored upper-cased");
        stored.FullName.Should().Be("Aya Kouassi", "structured names win over the captured label");
    }

    [Fact]
    public async Task A_second_save_adds_to_the_first_instead_of_erasing_it()
    {
        // Enrichment happens over several conversations, from a screen that posts only what it
        // collected. A null that cleared would make each save erase the last.
        var lead = await SeedAsync(withNames: true);

        await SendAsync(new EnrichLeadCommand(lead.Id, City: "Abidjan"));
        await SendAsync(new EnrichLeadCommand(lead.Id, BirthDate: new DateOnly(1990, 4, 2)));

        var stored = await ReloadAsync(lead.Id);
        stored.PrincipalCity.Should().Be("Abidjan");
        stored.DateOfBirth.Should().Be(new DateOnly(1990, 4, 2));
    }

    [Fact]
    public async Task Blank_is_how_a_field_is_cleared()
    {
        var lead = await SeedAsync(withNames: true);
        await SendAsync(new EnrichLeadCommand(lead.Id, City: "Abidjan"));

        await SendAsync(new EnrichLeadCommand(lead.Id, City: "   "));

        (await ReloadAsync(lead.Id)).PrincipalCity.Should().BeNull();
    }

    [Fact]
    public async Task It_answers_with_the_recomputed_completeness_report()
    {
        var lead = await SeedAsync(withNames: true);

        var result = await SendAsync(new EnrichLeadCommand(lead.Id, ProductLineId: _line));

        result.Value.LeadId.Should().Be(lead.Id);
        result.Value.Gaps.Should().NotContain(g =>
            g.Code == Sankore.Modules.Leads.Features.Enrichment
                      .CompletenessCodes.ProductLineNotResolved,
            "the line was just resolved, and the report has to reflect the write it accompanies");
    }

    // ── the product line is the one validated field ─────────────────────────

    [Fact]
    public async Task A_product_line_the_catalogue_does_not_have_is_refused()
    {
        // The only server-validated field here, because it decides which policy governs the lead.
        var lead = await SeedAsync(withNames: true);
        _admin.GetBeneficiaryPolicyAsync(_tenant, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
              .Returns((BeneficiaryPolicyDto?)null);

        var result = await SendAsync(new EnrichLeadCommand(lead.Id, ProductLineId: Guid.NewGuid()));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(EnrichLeadHandler.ProductLineNotFound);
        (await ReloadAsync(lead.Id)).ProductLineId.Should().BeNull();
    }

    [Fact]
    public async Task An_unreachable_catalogue_refuses_rather_than_storing_an_unchecked_id()
    {
        var lead = await SeedAsync(withNames: true);
        _admin.GetBeneficiaryPolicyAsync(_tenant, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
              .Returns<Task<BeneficiaryPolicyDto?>>(_ => throw new InvalidOperationException("M12 down"));

        var result = await SendAsync(new EnrichLeadCommand(lead.Id, ProductLineId: _line));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(HouseholdPolicyResolver.PolicyUnavailable);
    }

    [Fact]
    public async Task Clearing_the_product_line_needs_no_catalogue_read()
    {
        var lead = await SeedAsync(withNames: true);
        await SendAsync(new EnrichLeadCommand(lead.Id, ProductLineId: _line));

        var result = await SendAsync(new EnrichLeadCommand(lead.Id, ClearProductLine: true));

        result.IsSuccess.Should().BeTrue(result.Error);
        (await ReloadAsync(lead.Id)).ProductLineId.Should().BeNull();
    }

    // ── traceability ────────────────────────────────────────────────────────

    [Fact]
    public async Task An_activity_belonging_to_another_lead_is_refused()
    {
        var lead = await SeedAsync(withNames: true);

        var result = await SendAsync(new EnrichLeadCommand(
            lead.Id, City: "Abidjan", SourceActivityId: Guid.NewGuid()));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(EnrichLeadHandler.ActivityNotFound,
            "an event naming an activity of another lead is worse than one naming none");
    }

    [Fact]
    public async Task The_event_carries_field_NAMES_and_never_a_value()
    {
        var lead = await SeedAsync(withNames: true);

        await SendAsync(new EnrichLeadCommand(
            lead.Id, BirthDate: new DateOnly(1990, 4, 2), AddressLine: "12 rue des Jardins"));

        var published = _publisher.ReceivedCalls()
            .Select(c => c.GetArguments()[0])
            .OfType<LeadEnrichedIntegrationEvent>()
            .Should().ContainSingle().Subject;

        published.LeadId.Should().Be(lead.Id);
        published.ChangedFields.Should().BeEquivalentTo(["BirthDate", "AddressLine"]);

        // The address and the birth date are exactly what the module encrypts; an outbox row
        // holding them would be a second, unencrypted copy nothing ever purges.
        System.Text.Json.JsonSerializer.Serialize(published)
            .Should().NotContain("Jardins").And.NotContain("1990");
    }

    // ── refusals ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_converted_lead_cannot_be_enriched()
    {
        var lead = await SeedAsync(withNames: true);

        await using (var db = _factory.CreateContext())
        {
            var tracked = await db.Leads.AsTracking()
                .FirstAsync(l => l.Id == lead.Id, CancellationToken.None);
            tracked.Convert(Guid.NewGuid());
            await db.SaveChangesAsync(CancellationToken.None);
        }

        var result = await SendAsync(new EnrichLeadCommand(lead.Id, City: "Abidjan"));

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task A_lead_somebody_else_holds_is_refused()
    {
        // Same perimeter as the household writes: LeadAccessGuard, fail-closed.
        var lead = await SeedAsync(withNames: true);

        await using var db = _factory.CreateContext();
        var stranger = Substitute.For<ICurrentUser>();
        stranger.Id.Returns(Guid.NewGuid());
        stranger.TenantId.Returns(_tenant);
        stranger.Roles.Returns([Roles.CommercialAgent.Code]);

        var context = new HouseholdWriteContext(
            db, new LeadAccessGuard(stranger, _admin), new HouseholdPolicyResolver(_admin),
            _admin, new ExpectedResponsePlanner(db, _admin, TimeProvider.System),
            _publisher, new FixedClock(Now), NullLogger<HouseholdWriteContext>.Instance);

        var handler = new EnrichLeadHandler(
            context, db, _admin, TestCompleteness.Calculator(db, _admin), stranger, _sender,
            Options.Create(new LeadModuleSettings()), _publisher);

        var result = await handler.Handle(
            new EnrichLeadCommand(lead.Id, City: "Abidjan"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(LeadAccessGuard.Forbidden);
    }

    [Fact]
    public async Task A_line_that_records_no_household_does_not_refuse_an_identity_write()
    {
        // The gate this once guarded against is gone — no product line refuses a household any
        // more — but the case is still worth pinning: enrichment goes through LoadLeadAsync, so an
        // identity write resolves no policy at all and cannot fail on one.
        var lead = await SeedAsync(withNames: true);
        _admin.GetBeneficiaryPolicyAsync(_tenant, _line, Arg.Any<CancellationToken>())
              .Returns(new BeneficiaryPolicyDto(
                  _line, "CPT-01", nameof(ProductCategory.CurrentAccount),
                  true, false, [], null, null, [], null, null));

        await SendAsync(new EnrichLeadCommand(lead.Id, ProductLineId: _line));
        var result = await SendAsync(new EnrichLeadCommand(lead.Id, City: "Abidjan"));

        result.IsSuccess.Should().BeTrue(result.Error);
        (await ReloadAsync(lead.Id)).PrincipalCity.Should().Be("Abidjan");
    }
}

/// <summary>A clock frozen at one instant, so an assertion on a timestamp is deterministic.</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
