namespace Sankore.Admin.Tests.TenantSignups;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sankore.Admin.Domain;
using Sankore.Admin.Features.TenantSignups;
using Sankore.Admin.Features.Tenants.TrialEnd;
using Sankore.Admin.Infrastructure;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Free-trial requests (<c>docs/essai-gratuit-socle.md</c>, lot 1): what the website may record, when a
/// name or an address is free, and what the platform's approval creates.
/// </summary>
public sealed class TenantSignupHandlersTests : IDisposable
{
    private const string Template = "https://{slug}.talisoftcrm.com";
    private static readonly string Hash = new('a', 64);

    private readonly string _name = Guid.NewGuid().ToString();
    private readonly Clock _clock = new(new DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.Zero));
    private readonly IOptions<TenantSignupOptions> _options =
        Options.Create(new TenantSignupOptions { AddressTemplate = Template, TrialDays = 30 });

    private AdminDbContext Db() => new(new DbContextOptionsBuilder<AdminDbContext>().UseInMemoryDatabase(_name).Options);

    public void Dispose()
    {
        using var db = Db();
        db.Database.EnsureDeleted();
    }

    // ── Creation ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_valid_request_waits_for_its_email_and_says_its_address()
    {
        var result = await CreateAsync(Request(email: " Awa.Kone@CaisseAvenir.CI "));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Fqdn.Should().Be("https://caisse-avenir.talisoftcrm.com");
        result.Value.TokenExpiresAt.Should().Be(_clock.Now.AddHours(48));

        await using var db = Db();
        var stored = await db.TenantSignups.SingleAsync();
        stored.Status.Should().Be(TenantSignupStatus.PendingEmail);
        stored.Email.Should().Be("awa.kone@caisseavenir.ci");
        stored.Country.Should().Be("CI");
    }

    [Fact]
    public async Task Every_bad_field_is_reported_at_once_in_french()
    {
        var result = await CreateAsync(Request(slug: "A", email: "pas-une-adresse", phone: "12", country: "Côte", tokenHash: "x")
            with { InstitutionName = " ", FirstName = "" });

        result.Error.Should().Be(TenantSignupErrors.Invalid);
        result.Errors.Should().ContainKeys("institutionName", "firstName", "email", "phone", "country", "tokenHash", "slug");
    }

    [Theory]
    [InlineData("www")]
    [InlineData("admin")]
    [InlineData("api")]
    public async Task A_reserved_name_is_refused(string slug) =>
        (await CreateAsync(Request(slug: slug))).Error.Should().Be(TrialSlug.Reserved);

    [Fact]
    public async Task A_name_an_institution_already_answers_on_is_taken()
    {
        await using (var db = Db())
        {
            db.Tenants.Add(Tenant.Create("Caisse Avenir", "root@caisse.ci", "https://caisse-avenir.talisoftcrm.com"));
            await db.SaveChangesAsync();
        }

        (await CreateAsync(Request())).Error.Should().Be(TrialSlug.Taken);
    }

    [Fact]
    public async Task An_open_request_holds_its_name_and_an_expired_one_frees_it()
    {
        (await CreateAsync(Request())).IsSuccess.Should().BeTrue();
        (await CreateAsync(Request(email: "autre@imf.ci", tokenHash: new string('b', 64)))).Error.Should().Be(TrialSlug.Taken);

        _clock.Now = _clock.Now.AddHours(49);
        (await CreateAsync(Request(email: "autre@imf.ci", tokenHash: new string('b', 64)))).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task An_email_already_root_of_an_institution_or_in_an_open_request_is_in_use()
    {
        await using (var db = Db())
        {
            db.Tenants.Add(Tenant.Create("Autre", "Awa.Kone@CaisseAvenir.ci", "https://crm.autre.ci"));
            await db.SaveChangesAsync();
        }

        (await CreateAsync(Request())).Error.Should().Be(TenantSignupErrors.EmailInUse);
    }

    [Fact]
    public async Task The_trial_is_closed_without_an_address_template()
    {
        var closed = Options.Create(new TenantSignupOptions());
        await using var db = Db();
        var result = await new CreateTenantSignupHandler(db, closed, _clock).Handle(new CreateTenantSignupCommand(Request()), default);
        result.Error.Should().Be(TenantSignupErrors.NotConfigured);
    }

    [Fact]
    public async Task Availability_tells_free_reserved_invalid_and_taken()
    {
        await CreateAsync(Request());
        await using var db = Db();
        var handler = new CheckTrialSlugHandler(db, _options, _clock);

        (await handler.Handle(new("mutuelle-plateau"), default)).Value!.Status.Should().Be(TrialSlugAvailabilityEndpoint.Available);
        (await handler.Handle(new("Caisse-Avenir"), default)).Value!.Status.Should().Be(TrialSlug.Taken);
        (await handler.Handle(new("support"), default)).Value!.Status.Should().Be(TrialSlug.Reserved);
        var invalid = (await handler.Handle(new("a.b"), default)).Value!;
        invalid.Status.Should().Be(TrialSlug.Invalid);
        invalid.Fqdn.Should().BeNull();
    }

    // ── Confirmation ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_link_confirms_once_and_a_second_click_changes_nothing()
    {
        await CreateAsync(Request());

        var first = await ConfirmAsync(Hash);
        first.Value!.NewlyConfirmed.Should().BeTrue();
        var second = await ConfirmAsync(Hash.ToUpperInvariant());
        second.IsSuccess.Should().BeTrue();
        second.Value!.NewlyConfirmed.Should().BeFalse();

        await using var db = Db();
        (await db.TenantSignups.SingleAsync()).ConfirmedAt.Should().Be(_clock.Now);
    }

    [Fact]
    public async Task An_unknown_or_expired_link_is_refused()
    {
        await CreateAsync(Request());
        (await ConfirmAsync(new string('c', 64))).Error.Should().Be(TenantSignupErrors.TokenUnknown);
        (await ConfirmAsync("not-a-hash")).Error.Should().Be(TenantSignupErrors.TokenUnknown);

        _clock.Now = _clock.Now.AddHours(48);
        (await ConfirmAsync(Hash)).Error.Should().Be(TenantSignupErrors.TokenExpired);
    }

    [Fact]
    public async Task A_refused_request_cannot_be_confirmed_any_more()
    {
        var id = (await CreateAsync(Request())).Value!.Id;
        (await RejectAsync(id, "Institution introuvable")).IsSuccess.Should().BeTrue();
        (await ConfirmAsync(Hash)).Error.Should().Be(TenantSignupErrors.AlreadyDecided);
    }

    // ── Approval ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_unconfirmed_request_cannot_be_approved()
    {
        var id = (await CreateAsync(Request())).Value!.Id;
        (await ApproveAsync(id)).Error.Should().Be(TenantSignupErrors.NotConfirmed);
    }

    [Fact]
    public async Task Approval_creates_a_socle_trial_institution_with_its_starter_data()
    {
        var id = (await CreateAsync(Request())).Value!.Id;
        await ConfirmAsync(Hash);
        var adminId = Guid.NewGuid();

        var result = await ApproveAsync(id, adminId);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Fqdn.Should().Be("https://caisse-avenir.talisoftcrm.com");
        result.Value.TrialExpiresAt.Should().Be(_clock.Now.AddDays(30));

        await using var db = Db();
        var tenant = await db.Tenants.SingleAsync();
        tenant.Id.Should().Be(result.Value.TenantId);
        tenant.Offer.Should().Be(TenantOffer.Trial);
        tenant.Name.Should().Be("Caisse Avenir");
        tenant.RootUserEmail.Should().Be("awa.kone@caisseavenir.ci");
        tenant.TrialExpiresAt.Should().Be(_clock.Now.AddDays(30));
        tenant.IsTest.Should().BeFalse();

        (await db.TenantDomains.SingleAsync()).IsPrimary.Should().BeTrue();
        var load = await db.TenantDataLoads.SingleAsync();
        load.TenantId.Should().Be(tenant.Id);
        load.Kind.Should().Be(TenantDataLoadKind.Starter);
        (await db.TenantModuleSubscriptions.AnyAsync()).Should().BeFalse();

        var signup = await db.TenantSignups.SingleAsync();
        signup.Status.Should().Be(TenantSignupStatus.Approved);
        signup.TenantId.Should().Be(tenant.Id);
        signup.DecidedByAdminId.Should().Be(adminId);

        (await ApproveAsync(id)).Error.Should().Be(TenantSignupErrors.AlreadyDecided);
    }

    [Fact]
    public async Task An_address_given_by_hand_in_the_meantime_stops_the_approval()
    {
        var id = (await CreateAsync(Request())).Value!.Id;
        await ConfirmAsync(Hash);
        await using (var db = Db())
        {
            db.Tenants.Add(Tenant.Create("Ailleurs", "root@ailleurs.ci", "https://caisse-avenir.talisoftcrm.com"));
            await db.SaveChangesAsync();
        }

        (await ApproveAsync(id)).Error.Should().Be(TrialSlug.Taken);
        await using var check = Db();
        (await check.TenantSignups.SingleAsync()).Status.Should().Be(TenantSignupStatus.Confirmed);
    }

    // ── Refusal and the decision feed ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_refusal_needs_a_reason()
    {
        var id = (await CreateAsync(Request())).Value!.Id;
        (await RejectAsync(id, "  ")).Error.Should().Be(TenantSignupErrors.ReasonRequired);
    }

    [Fact]
    public async Task Decisions_are_fed_to_the_api_until_marked_as_told()
    {
        var approved = (await CreateAsync(Request())).Value!.Id;
        await ConfirmAsync(Hash);
        await ApproveAsync(approved);
        var rejected = (await CreateAsync(Request(slug: "autre-imf", email: "x@autre.ci", tokenHash: new string('d', 64)))).Value!.Id;
        await RejectAsync(rejected, "Doublon");
        await CreateAsync(Request(slug: "en-attente", email: "y@autre.ci", tokenHash: new string('e', 64)));

        await using (var db = Db())
        {
            var feed = await new ListTenantSignupDecisionsHandler(db, _options).Handle(new(50), default);
            feed.Select(d => d.Id).Should().BeEquivalentTo([approved, rejected]);
            feed.Single(d => d.Id == approved).Fqdn.Should().Be("https://caisse-avenir.talisoftcrm.com");
            feed.Single(d => d.Id == rejected).RejectionReason.Should().Be("Doublon");

            (await new MarkTenantSignupDecisionNotifiedHandler(db, _clock).Handle(new(approved), default)).IsSuccess.Should().BeTrue();
        }

        await using (var db = Db())
        {
            var feed = await new ListTenantSignupDecisionsHandler(db, _options).Handle(new(50), default);
            feed.Select(d => d.Id).Should().Equal(rejected);
        }
    }

    [Fact]
    public async Task The_queue_counts_expired_requests_apart()
    {
        await CreateAsync(Request());
        await CreateAsync(Request(slug: "autre-imf", email: "x@autre.ci", tokenHash: new string('d', 64)));
        await ConfirmAsync(Hash);
        _clock.Now = _clock.Now.AddDays(3);

        await using var db = Db();
        var counts = await new CountTenantSignupsHandler(db, _clock).Handle(new(), default);
        counts.ConfirmedCount.Should().Be(1);
        counts.ExpiredCount.Should().Be(1);
        counts.PendingEmailCount.Should().Be(0);

        var expired = await new ListTenantSignupsHandler(db, _options, _clock)
            .Handle(new(TenantSignupView.Expired, null, null, null), default);
        expired.Items.Should().ContainSingle().Which.Slug.Should().Be("autre-imf");
    }

    // ── Trial end, moved from the institution's page ────────────────────────────────────────────

    [Fact]
    public async Task The_platform_prolongs_a_trial_but_never_into_the_past_nor_on_a_paying_institution()
    {
        var id = (await CreateAsync(Request())).Value!.Id;
        await ConfirmAsync(Hash);
        var tenantId = (await ApproveAsync(id)).Value!.TenantId;
        Guid paidId;
        await using (var db = Db())
        {
            var paid = Tenant.Create("Client payant", "root@payant.ci", "https://payant.talisoftcrm.com");
            db.Tenants.Add(paid);
            await db.SaveChangesAsync();
            paidId = paid.Id;
        }

        async Task<string?> Move(Guid tenant, DateTimeOffset end)
        {
            await using var db = Db();
            return await new SetTrialEndHandler(db, _clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<SetTrialEndHandler>.Instance)
                .Handle(new SetTrialEndCommand(tenant, end), default);
        }

        (await Move(tenantId, _clock.Now.AddDays(45))).Should().BeNull();
        (await Move(tenantId, _clock.Now.AddMinutes(-1))).Should().Be(SetTrialEndEndpoint.EndInPast);
        (await Move(paidId, _clock.Now.AddDays(10))).Should().Be(SetTrialEndEndpoint.NotATrial);
        (await Move(Guid.NewGuid(), _clock.Now.AddDays(10))).Should().Be("NOT_FOUND");

        await using var check = Db();
        (await check.Tenants.SingleAsync(t => t.Id == tenantId)).TrialExpiresAt.Should().Be(_clock.Now.AddDays(45));
    }

    // ── Options ─────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://{slug}.talisoftcrm.com", "https://{slug}.talisoftcrm.com")]
    [InlineData(" HTTPS://{slug}.TalisoftCRM.com/ ", "https://{slug}.talisoftcrm.com")]
    [InlineData("http://{slug}.localhost:4222", "http://{slug}.localhost:4222")]
    [InlineData("", null)]
    [InlineData("https://talisoftcrm.com", null)]                 // no {slug}
    [InlineData("https://crm.{slug}.talisoftcrm.com", null)]      // not the first label
    [InlineData("https://{slug}.{slug}.talisoftcrm.com", null)]
    [InlineData("https://{slug}.talisoftcrm.com/essai", null)]    // a path
    [InlineData("ftp://{slug}.talisoftcrm.com", null)]
    public void The_address_template_must_be_an_origin_starting_with_the_name(string template, string? expected) =>
        new TenantSignupOptions { AddressTemplate = template }.ValidTemplate().Should().Be(expected);

    [Theory]
    [InlineData("caisse-avenir", null)]
    [InlineData("imf2", null)]
    [InlineData("ab", TrialSlug.Invalid)]
    [InlineData("-caisse", TrialSlug.Invalid)]
    [InlineData("caisse-", TrialSlug.Invalid)]
    [InlineData("caisse--avenir", TrialSlug.Invalid)]
    [InlineData("caisse.avenir", TrialSlug.Invalid)]
    [InlineData("crédit", TrialSlug.Invalid)]
    [InlineData("une-adresse-bien-trop-longue-pour-un-crm", TrialSlug.Invalid)]
    [InlineData("demo", TrialSlug.Reserved)]
    public void Names_follow_one_rule(string slug, string? expected) =>
        TrialSlug.Check(TrialSlug.Normalize(slug)).Should().Be(expected);

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private static CreateTenantSignupEndpoint.Request Request(string slug = "caisse-avenir",
        string email = "awa.kone@caisseavenir.ci", string phone = "+225 07 08 09 10 11", string country = "ci",
        string? tokenHash = null) => new(
        "Caisse Avenir", slug, "Awa", "Koné", email, phone, country, "Microfinance",
        tokenHash ?? Hash, "198.51.100.7", "Mozilla/5.0");

    private async Task<TenantSignupResult<CreateTenantSignupEndpoint.Response>> CreateAsync(CreateTenantSignupEndpoint.Request request)
    {
        await using var db = Db();
        return await new CreateTenantSignupHandler(db, _options, _clock).Handle(new CreateTenantSignupCommand(request), default);
    }

    private async Task<TenantSignupResult<ConfirmTenantSignupEndpoint.Response>> ConfirmAsync(string hash)
    {
        await using var db = Db();
        return await new ConfirmTenantSignupHandler(db, _clock).Handle(new ConfirmTenantSignupCommand(hash), default);
    }

    private async Task<TenantSignupResult<ApprovedTenantSignup>> ApproveAsync(Guid id, Guid? adminId = null)
    {
        await using var db = Db();
        return await new ApproveTenantSignupHandler(db, _options, _clock).Handle(new ApproveTenantSignupCommand(id, adminId), default);
    }

    private async Task<TenantSignupResult<bool>> RejectAsync(Guid id, string reason)
    {
        await using var db = Db();
        return await new RejectTenantSignupHandler(db, _clock).Handle(new RejectTenantSignupCommand(id, reason, null), default);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
