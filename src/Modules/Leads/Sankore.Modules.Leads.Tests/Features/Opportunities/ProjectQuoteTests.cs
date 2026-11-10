namespace Sankore.Modules.Leads.Tests.Features.Opportunities;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Opportunities;
using Sankore.Modules.Leads.Features.Opportunities.History;
using Sankore.Modules.Leads.Features.Opportunities.Quote;
using Sankore.Modules.Leads.Features.Opportunities.UpdateOpportunity;
using Sankore.Modules.Leads.Features.ProjectTemplates;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Infrastructure.TaskAttachments;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// Quote lines, lot 3 (2026-11-10, product owner): several Standard products on a project, their
/// discounts and VAT, the totals, the project's amount and product following them, and a manager's
/// decision on a discount beyond the institution's ceiling.
/// </summary>
public sealed class ProjectQuoteTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _agent = Guid.NewGuid();
    private readonly Guid _manager = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();
    private readonly IQuoteIssuer _issuer = Substitute.For<IQuoteIssuer>();
    private readonly INotificationsModule _notifications = Substitute.For<INotificationsModule>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-10T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private static readonly Guid Cement = Guid.NewGuid();
    private static readonly Guid Steel = Guid.NewGuid();
    private static readonly Guid Install = Guid.NewGuid();
    private static readonly Guid Credit = Guid.NewGuid();

    public ProjectQuoteTests()
    {
        _factory = new TestDbContextFactory(_tenantId);
        ActAs(_agent, "CommercialAgent");
        Issuer(ceiling: 10m);
        _issuer.GetProductsAsync(_tenantId, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => Catalogue.Where(p => ci.ArgAt<IReadOnlyCollection<Guid>>(1).Contains(p.Id)).ToList());
        _admin.GetUserSupervisorsAsync(_tenantId, _agent, Arg.Any<CancellationToken>()).Returns(new UserSupervisors(_manager, null));
        _admin.GetTeamAgentIdsAsync(_tenantId, _manager, Arg.Any<CancellationToken>()).Returns([_agent]);
        _admin.GetUserNamesAsync(_tenantId, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<IReadOnlyCollection<Guid>>(1).ToDictionary(id => id, _ => "Koffi Manager"));
    }

    private static readonly QuotableProduct[] Catalogue =
    [
        new(Cement, "CIM-45", "Ciment CPJ 45", "Standard", true, 4_750m, "sac", ProductVatCategory.Standard),
        new(Steel, "FER-12", "Fer à béton 12 mm", "Standard", true, 6_000m, "barre", ProductVatCategory.Reduced),
        new(Install, "POSE", "Pose", "Standard", true, null, "heure", ProductVatCategory.Exempt),
        new(Credit, "CRED-IMMO", "Crédit immobilier", "Loan", true, null, null, ProductVatCategory.Standard),
    ];

    public void Dispose() => _factory.Dispose();

    private void ActAs(Guid id, string role, params string[] permissions)
    {
        _user.Id.Returns(id);
        _user.TenantId.Returns(_tenantId);
        _user.IsAuthenticated.Returns(true);
        _user.Roles.Returns([role]);
        _user.Permissions.Returns(permissions);
    }

    private void Issuer(decimal? ceiling) =>
        _issuer.GetAsync(_tenantId, Arg.Any<CancellationToken>()).Returns(new QuoteIssuer(
            "Lagune Négoce", null, null, null, null, null, null, null, null, null, null,
            "XOF", 18m, 9m, ceiling, "DEV", 30, null));

    private async Task<Guid> SeedProjectAsync(string product = "", decimal? amount = null)
    {
        await using var db = _factory.CreateContext();
        var project = Opportunity.CreateFromLead(_tenantId, Guid.NewGuid(), "Chantier Lagune", product, _agent,
            TimeProvider.System, estimatedAmount: amount is { } a ? new Money(a, "XOF") : null);
        if (product == "Crédit immobilier") project.SetProductLine(Credit);
        db.Opportunities.Add(project);
        await db.SaveChangesAsync();
        return project.Id;
    }

    private ProjectQuotes Quotes(LeadsDbContext db) =>
        new(db, _issuer, _admin, _user, new ProjectTemplateResolver(db, _admin, new FixedTenantContext(_tenantId)));

    private async Task<Result<ProjectQuoteDto>> SetAsync(Guid projectId, IReadOnlyList<ProjectQuoteLineInput> lines,
        QuoteDiscountKind global = QuoteDiscountKind.None, decimal globalValue = 0)
    {
        await using var db = _factory.CreateContext();
        return await new SetProjectQuoteHandler(db, Quotes(db), _issuer, _admin,
                new ProjectTemplateResolver(db, _admin, new FixedTenantContext(_tenantId)),
                new ProjectHistoryRecorder(db, _user, new FixedTime(Now)), _user, new FixedTime(Now),
                NullLogger<SetProjectQuoteHandler>.Instance, _notifications)
            .Handle(new SetProjectQuoteCommand(projectId, lines, global, globalValue, null), CancellationToken.None);
    }

    private async Task<Result<ProjectQuoteDto>> DecideAsync(Guid projectId, bool approve, string? comment = null)
    {
        await using var db = _factory.CreateContext();
        return await new DecideProjectQuoteHandler(db, Quotes(db), new ProjectHistoryRecorder(db, _user, new FixedTime(Now)),
                _user, new FixedTime(Now), NullLogger<DecideProjectQuoteHandler>.Instance, _notifications)
            .Handle(new DecideProjectQuoteCommand(projectId, approve, comment), CancellationToken.None);
    }

    [Fact]
    public async Task Lines_are_priced_from_the_catalogue_and_totalled_with_vat_by_rate()
    {
        var id = await SeedProjectAsync();

        var result = await SetAsync(id, [
            new(null, Cement, 100),                             // 475 000 HT, 18 %
            new(null, Steel, 10, DiscountKind: QuoteDiscountKind.Percent, DiscountValue: 5), // 60 000 − 3 000 = 57 000, 9 %
            new(null, Install, 8, UnitPrice: 5_000m),           // 40 000, exonéré
        ]);

        result.IsSuccess.Should().BeTrue(result.Error);
        var q = result.Value;
        q.Lines.Select(l => l.NetExclTax).Should().Equal(475_000m, 57_000m, 40_000m);
        q.Lines.Select(l => l.VatRate).Should().Equal(18m, 9m, 0m);
        q.Lines[0].Unit.Should().Be("sac");
        q.Lines[0].Label.Should().Be("Ciment CPJ 45");
        q.TotalExclTax.Should().Be(572_000m);
        q.TotalVat.Should().Be(85_500m + 5_130m);
        q.TotalInclTax.Should().Be(662_630m);
        q.VatByRate.Should().HaveCount(3);
        q.ApprovalStatus.Should().Be(QuoteApprovalStatus.NotRequired);

        await using var db = _factory.CreateContext();
        var project = await db.Opportunities.SingleAsync(o => o.Id == id);
        project.EstimatedAmount.Should().Be(new Money(572_000m, "XOF"));
        project.Product.Should().Be("Ciment CPJ 45");
        project.ProductLineId.Should().Be(Cement);
        (await db.ProjectHistory.Where(h => h.OpportunityId == id).Select(h => h.Kind).ToListAsync())
            .Should().Contain([ProjectHistoryKind.QuoteLinesChanged, ProjectHistoryKind.AmountChanged, ProjectHistoryKind.ProductChanged]);
    }

    [Fact]
    public async Task The_global_discount_comes_before_the_vat()
    {
        var id = await SeedProjectAsync();

        var q = (await SetAsync(id, [new(null, Cement, 100)], QuoteDiscountKind.Amount, 25_000m)).Value;

        q.SubtotalExclTax.Should().Be(475_000m);
        q.GlobalDiscountAmount.Should().Be(25_000m);
        q.TotalExclTax.Should().Be(450_000m);
        q.TotalVat.Should().Be(81_000m);
        q.TotalInclTax.Should().Be(531_000m);
    }

    [Fact]
    public void Cfa_amounts_are_rounded_to_the_franc()
    {
        var line = ProjectQuoteLine.Create(null, Cement, "CIM-45", "Ciment", 3, null, 333.33m, 333.33m,
            QuoteDiscountKind.None, 0, ProductVatCategory.Standard, 18m).Value;

        var totals = QuoteTotals.Compute([line], QuoteDiscountKind.None, 0, "XOF").Value;

        line.GrossExclTax.Should().Be(1_000m);
        totals.TotalVat.Should().Be(180m);
    }

    [Fact]
    public async Task A_price_cut_below_the_catalogue_counts_as_a_discount_and_asks_the_manager()
    {
        var id = await SeedProjectAsync();

        var q = (await SetAsync(id, [new(null, Cement, 100, UnitPrice: 4_000m)])).Value;

        q.EffectiveDiscountPercent.Should().Be(15.79m);
        q.ApprovalStatus.Should().Be(QuoteApprovalStatus.Pending);
        q.CanBeIssued.Should().BeFalse();
        await _notifications.Received(1).NotifyInAppAsync(
            Arg.Is<InAppNotificationRequest>(r => r.UserId == _manager && r.Link == $"/leads/projets/{id}"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Within_the_ceiling_or_without_one_nothing_is_asked()
    {
        var id = await SeedProjectAsync();
        (await SetAsync(id, [new(null, Cement, 10, DiscountKind: QuoteDiscountKind.Percent, DiscountValue: 10)]))
            .Value.ApprovalStatus.Should().Be(QuoteApprovalStatus.NotRequired);

        Issuer(ceiling: null);
        (await SetAsync(id, [new(null, Cement, 10, DiscountKind: QuoteDiscountKind.Percent, DiscountValue: 60)]))
            .Value.ApprovalStatus.Should().Be(QuoteApprovalStatus.NotRequired);
    }

    [Fact]
    public async Task The_owners_manager_approves_and_a_new_change_asks_again()
    {
        var id = await SeedProjectAsync();
        await SetAsync(id, [new(null, Cement, 100, DiscountKind: QuoteDiscountKind.Percent, DiscountValue: 20)]);

        ActAs(_manager, "SalesManager", Permissions.CanSuperviseTeam.Code);
        var approved = await DecideAsync(id, approve: true, "Client stratégique");
        approved.IsSuccess.Should().BeTrue(approved.Error);
        approved.Value.ApprovalStatus.Should().Be(QuoteApprovalStatus.Approved);
        approved.Value.DecidedByName.Should().Be("Koffi Manager");
        approved.Value.CanBeIssued.Should().BeTrue();

        ActAs(_agent, "CommercialAgent");
        var changed = (await SetAsync(id, [new(approved.Value.Lines[0].Id, Cement, 120, DiscountKind: QuoteDiscountKind.Percent, DiscountValue: 20)])).Value;
        changed.ApprovalStatus.Should().Be(QuoteApprovalStatus.Pending);
    }

    [Fact]
    public async Task Nobody_approves_their_own_discount_nor_one_outside_their_team()
    {
        var id = await SeedProjectAsync();
        await SetAsync(id, [new(null, Cement, 100, DiscountKind: QuoteDiscountKind.Percent, DiscountValue: 20)]);

        ActAs(_agent, "CommercialAgent", Permissions.CanSuperviseTeam.Code);
        (await DecideAsync(id, true)).Error.Should().Be("QUOTE_SELF_APPROVAL_FORBIDDEN");

        ActAs(_agent, "CommercialAgent");
        (await DecideAsync(id, true)).Error.Should().Be("QUOTE_APPROVER_NOT_ALLOWED");

        var stranger = Guid.NewGuid();
        _admin.GetTeamAgentIdsAsync(_tenantId, stranger, Arg.Any<CancellationToken>()).Returns([]);
        ActAs(stranger, "BranchManager", Permissions.CanSuperviseTeam.Code);
        (await DecideAsync(id, true)).Error.Should().Be("QUOTE_APPROVER_NOT_ALLOWED");
    }

    [Fact]
    public async Task A_refusal_needs_a_reason()
    {
        var id = await SeedProjectAsync();
        await SetAsync(id, [new(null, Cement, 100, DiscountKind: QuoteDiscountKind.Percent, DiscountValue: 20)]);
        ActAs(_manager, "SalesManager", Permissions.CanSuperviseTeam.Code);

        (await DecideAsync(id, false)).Error.Should().Be("QUOTE_REJECTION_REASON_REQUIRED");
        (await DecideAsync(id, false, "Marge insuffisante")).Value.ApprovalStatus.Should().Be(QuoteApprovalStatus.Rejected);
    }

    [Fact]
    public async Task A_saved_line_keeps_its_rate_when_the_institution_changes_it()
    {
        var id = await SeedProjectAsync();
        var first = (await SetAsync(id, [new(null, Cement, 1)])).Value;

        _issuer.GetAsync(_tenantId, Arg.Any<CancellationToken>()).Returns(new QuoteIssuer(
            "Lagune Négoce", null, null, null, null, null, null, null, null, null, null,
            "XOF", 20m, 10m, 10m, "DEV", 30, null));
        var again = (await SetAsync(id, [new(first.Lines[0].Id, Cement, 2), new(null, Cement, 1)])).Value;

        again.Lines.Select(l => l.VatRate).Should().Equal(18m, 20m);
    }

    [Fact]
    public async Task Module_products_and_finance_projects_take_no_lines()
    {
        var id = await SeedProjectAsync();
        (await SetAsync(id, [new(null, Credit, 1, UnitPrice: 1m)])).Error.Should().Be("QUOTE_PRODUCT_NOT_STANDARD");

        var credit = await SeedProjectAsync("Crédit immobilier");
        (await SetAsync(credit, [new(null, Cement, 1)])).Error.Should().Be("QUOTE_FINANCE_PROJECT");
    }

    [Fact]
    public async Task A_product_without_price_needs_one_typed()
    {
        var id = await SeedProjectAsync();
        (await SetAsync(id, [new(null, Install, 1)])).Error.Should().Be("QUOTE_LINE_PRICE_REQUIRED");
    }

    [Fact]
    public async Task A_discount_above_the_amount_is_refused()
    {
        var id = await SeedProjectAsync();
        (await SetAsync(id, [new(null, Cement, 1, DiscountKind: QuoteDiscountKind.Amount, DiscountValue: 5_000m)]))
            .Error.Should().Be("QUOTE_DISCOUNT_ABOVE_AMOUNT");
    }

    [Fact]
    public async Task While_lines_drive_the_amount_it_cannot_be_typed()
    {
        var id = await SeedProjectAsync();
        await SetAsync(id, [new(null, Cement, 100)]);

        await using var db = _factory.CreateContext();
        var handler = new UpdateOpportunityHandler(db, new FixedTime(Now));
        (await handler.Handle(new UpdateOpportunityCommand(id, EstimatedAmount: 1m), CancellationToken.None))
            .Error.Should().Be("PROJECT_AMOUNT_FROM_QUOTE_LINES");
        (await handler.Handle(new UpdateOpportunityCommand(id, Title: "Chantier", EstimatedAmount: 475_000m), CancellationToken.None))
            .IsSuccess.Should().BeTrue();
    }

    // ── Lot 5: the quote PDF ────────────────────────────────────────────────

    private readonly MemoryStore _store = new();

    private async Task<Result<ProjectQuoteDto>> IssueAsync(Guid projectId, int? days = null, string? terms = null)
    {
        await using var db = _factory.CreateContext();
        return await new IssueProjectQuoteHandler(db, Quotes(db), _issuer,
                new ProjectHistoryRecorder(db, _user, new FixedTime(Now)), _user, new FixedTime(Now),
                new Services((typeof(ITaskAttachmentStore), _store)), NullLogger<IssueProjectQuoteHandler>.Instance)
            .Handle(new IssueProjectQuoteCommand(projectId, days, terms), CancellationToken.None);
    }

    [Fact]
    public async Task The_first_pdf_takes_a_number_and_the_next_ones_a_version()
    {
        var id = await SeedProjectAsync();
        await SetAsync(id, [new(null, Cement, 100), new(null, Install, 8, UnitPrice: 5_000m)]);
        _issuer.NextNumberAsync(_tenantId, Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns("DEV-2026-000007");

        var first = await IssueAsync(id, days: 15, terms: "50 % à la commande");
        first.IsSuccess.Should().BeTrue(first.Error);
        first.Value.Number.Should().Be("DEV-2026-000007");
        first.Value.Issues!.Single().Label.Should().Be("DEV-2026-000007");
        first.Value.Issues!.Single().ValidUntil.Should().Be(new DateOnly(2026, 11, 25));

        var second = (await IssueAsync(id)).Value;
        second.Issues!.Select(i => i.Label).Should().Equal("DEV-2026-000007 v2", "DEV-2026-000007");
        await _issuer.Received(1).NextNumberAsync(_tenantId, Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());

        await using var db = _factory.CreateContext();
        var files = await db.ProjectAttachments.Where(a => a.OpportunityId == id).ToListAsync();
        files.Should().HaveCount(2).And.OnlyContain(f => f.FieldKey == "quote" && f.ContentType == "application/pdf");
        var pdf = await _store.OpenAsync(_tenantId, files[0].StorageRef, CancellationToken.None);
        System.Text.Encoding.ASCII.GetString(pdf!, 0, 4).Should().Be("%PDF");
        (await db.ProjectHistory.CountAsync(h => h.OpportunityId == id && h.Kind == ProjectHistoryKind.QuoteIssued)).Should().Be(2);
    }

    [Fact]
    public async Task No_pdf_while_the_discount_waits_or_without_lines()
    {
        var id = await SeedProjectAsync();
        (await IssueAsync(id)).Error.Should().Be("QUOTE_EMPTY");

        await SetAsync(id, [new(null, Cement, 100, DiscountKind: QuoteDiscountKind.Percent, DiscountValue: 20)]);
        (await IssueAsync(id)).Error.Should().Be("QUOTE_DISCOUNT_NOT_APPROVED");
    }

    [Fact]
    public async Task Changing_the_lines_after_a_pdf_is_said()
    {
        var id = await SeedProjectAsync();
        await SetAsync(id, [new(null, Cement, 100)]);
        _issuer.NextNumberAsync(_tenantId, Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns("DEV-2026-000008");
        (await IssueAsync(id)).Value.ChangedSinceIssue.Should().BeFalse();

        var later = Now.AddMinutes(5);
        await using var db = _factory.CreateContext();
        var changed = await new SetProjectQuoteHandler(db, Quotes(db), _issuer, _admin,
                new ProjectTemplateResolver(db, _admin, new FixedTenantContext(_tenantId)),
                new ProjectHistoryRecorder(db, _user, new FixedTime(later)), _user, new FixedTime(later),
                NullLogger<SetProjectQuoteHandler>.Instance, _notifications)
            .Handle(new SetProjectQuoteCommand(id, [new(null, Cement, 120)], QuoteDiscountKind.None, 0, null), CancellationToken.None);
        changed.Value.ChangedSinceIssue.Should().BeTrue();
    }

    [Fact]
    public void The_pdf_prints_with_every_optional_part()
    {
        var line = ProjectQuoteLine.Create(null, Cement, "CIM-45", "Ciment CPJ 45", 100, "sac", 4_750m, 4_750m,
            QuoteDiscountKind.Percent, 5, ProductVatCategory.Standard, 18m).Value;
        var exempt = ProjectQuoteLine.Create(null, Install, "POSE", "Pose", 8, "heure", 5_000m, null,
            QuoteDiscountKind.None, 0, ProductVatCategory.Exempt, 0m).Value;
        var quote = ProjectQuote.Start(_tenantId, Guid.NewGuid(), "XOF", Now);
        quote.Replace([line, exempt], QuoteDiscountKind.Amount, 10_000m, null, null, Now).IsSuccess.Should().BeTrue();
        var totals = QuoteTotals.Compute(quote.Lines, quote.GlobalDiscountKind, quote.GlobalDiscountValue, "XOF").Value;
        var issuer = new QuoteIssuer("Lagune Négoce", "Lagune Négoce SA", "SA", "Rue du Commerce\nBP 123 Abidjan 01",
            "+225 27 20 00 00 00", "devis@lagune.ci", "www.lagune.ci", "CI-ABJ-2019-B-12345", "1912345 A", "Réel normal",
            "Capital 10 000 000 FCFA", "XOF", 18m, 9m, 10m, "DEV", 30, "Comptant");

        var pdf = ProjectQuotePdf.Generate(new QuotePdfModel(issuer, new QuoteLogo([1, 2, 3], "image/png"),
            new QuoteRecipient("BTP Lagune Construction", "Ibrahim Sanogo", "Abidjan", null), "Chantier Lagune",
            "DEV-2026-000001 v2", new DateOnly(2026, 11, 10), new DateOnly(2026, 12, 10), "Comptant", quote, totals, TestMode: true));

        pdf.Length.Should().BeGreaterThan(1_000);
        System.Text.Encoding.ASCII.GetString(pdf, 0, 4).Should().Be("%PDF");
    }

    private sealed class MemoryStore : ITaskAttachmentStore
    {
        private readonly Dictionary<string, byte[]> _files = new();

        public Task<StoredTaskAttachment> StoreAsync(Guid tenantId, byte[] content, CancellationToken ct)
        {
            var reference = Guid.NewGuid().ToString("N");
            _files[reference] = content;
            return Task.FromResult(new StoredTaskAttachment(reference, content.Length, "sha"));
        }

        public Task<byte[]?> OpenAsync(Guid tenantId, string storageRef, CancellationToken ct) => Task.FromResult(_files.GetValueOrDefault(storageRef));

        public Task<bool> DeleteAsync(Guid tenantId, string storageRef, CancellationToken ct) => Task.FromResult(_files.Remove(storageRef));
    }

    private sealed class Services(params (Type Type, object Service)[] services) : IServiceProvider
    {
        public object? GetService(Type serviceType) => services.FirstOrDefault(s => s.Type == serviceType).Service;
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
