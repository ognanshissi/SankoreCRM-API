namespace Sankore.Modules.Customers.Tests.Features.Holdings;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Holdings.Designate;
using Sankore.Modules.Customers.Features.Holdings.GetHolding;
using Sankore.Modules.Customers.Features.Holdings.ListHoldings;
using Sankore.Modules.Customers.Features.Holdings.Shared;
using Sankore.Modules.Customers.Features.Holdings.Subscribe;
using Sankore.Modules.Customers.Features.Holdings.Transitions;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Compliance;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Subscribing a client to a product and designating on it, through the handlers (BNF-B-14/15) —
/// which is where the load-modify-save path lives that the aggregate tests cannot reach.
/// </summary>
public sealed class ProductHoldingHandlerTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _agency = Guid.NewGuid();
    private readonly Guid _actor = Guid.NewGuid();
    private readonly Guid _lineId = Guid.NewGuid();
    private readonly TestCustomersDbContextFactory _factory;
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();
    private readonly IEventPublisher _publisher = Substitute.For<IEventPublisher>();
    private readonly Guid _clientId;

    public ProductHoldingHandlerTests()
    {
        _factory = new TestCustomersDbContextFactory(_tenant);
        Line();

        using var db = _factory.CreateContext();
        var client = TestClientFactory.Individual(_tenant, _agency);
        db.Clients.Add(client);
        db.SaveChanges();
        _clientId = client.Id;
    }

    public void Dispose() => _factory.Dispose();

    // ── fixtures ────────────────────────────────────────────────────────────

    // A savings line with a death beneficiary: an insurance line cannot be subscribed through
    // this handler any more — it comes from a won project.
    private void Line(
        bool allowMultipleHoldings = true,
        IReadOnlyList<DesignationRoleDto>? roles = null,
        string category = nameof(ProductCategory.Savings))
        => _admin.GetBeneficiaryPolicyAsync(_tenant, _lineId, Arg.Any<CancellationToken>())
                 .Returns(TestHoldingFactory.Line(
                     productLineId: _lineId,
                     category: category,
                     allowMultipleHoldings: allowMultipleHoldings,
                     roles: roles));

    private HoldingWriteContext Context(CustomersDbContext db)
        => TestHouseholdContext.Holdings(db, _tenant, _actor, _admin);

    private async Task<Result<Guid>> SubscribeAsync(
        IReadOnlyList<DesignationInput>? designations = null)
    {
        await using var db = _factory.CreateContext();

        var handler = new SubscribeToProductHandler(
            db, Context(db), new DesignationSubjectValidator(db));

        return await handler.Handle(
            new SubscribeToProductCommand(
                _clientId, _lineId,
                new HoldingSpecificsDto(InsuredCapital: 5_000_000m, Currency: "XOF"),
                designations),
            CancellationToken.None);
    }

    private async Task<Result<DesignateResult>> DesignateAsync(
        Guid holdingId, IReadOnlyList<DesignationInput> designations)
    {
        await using var db = _factory.CreateContext();
        var handler = new DesignateHandler(
            Context(db), new DesignationSubjectValidator(db), _publisher);
        return await handler.Handle(new DesignateCommand(holdingId, designations), CancellationToken.None);
    }

    private static DesignationInput Beneficiary(decimal share, string name = "Awa Ouattara")
        => new(
            RoleCode: "BENEFICIAIREDECES",
            SubjectType: nameof(DesignationSubjectType.ExternalPerson),
            SubjectId: null,
            ExternalFullName: name,
            ExternalRelationship: "Épouse",
            Rank: 1,
            SharePercent: share);

    private T Published<T>() => _publisher.ReceivedCalls()
        .Select(c => c.GetArguments()[0]).OfType<T>().Single();

    // ── subscribing ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_subscription_snapshots_the_line_and_starts_as_a_draft()
    {
        var result = await SubscribeAsync();

        result.IsSuccess.Should().BeTrue(result.Error);

        await using var db = _factory.CreateContext();
        var holding = await db.ProductHoldings.SingleAsync(CancellationToken.None);

        holding.Status.Should().Be(HoldingStatus.Draft);
        holding.ProductLineId.Should().Be(_lineId);
        holding.ProductCode.Should().Be("VIE-01", "a snapshot, so a renamed product does not rewrite a contract");
        holding.ProductCategory.Should().Be(nameof(ProductCategory.Savings));
        holding.Specifics.InsuredCapital.Should().Be(5_000_000m);
        holding.Specifics.Currency.Should().Be("XOF");
    }

    [Theory]
    [InlineData(nameof(ProductCategory.Insurance))]
    [InlineData(nameof(ProductCategory.HealthInsurance))]
    [InlineData(nameof(ProductCategory.ForecastInsurance))]
    public async Task An_insurance_line_is_subscribed_through_a_project_not_from_the_client_file(string category)
    {
        Line(category: category);

        (await SubscribeAsync()).Error.Should().Be(CustomerErrors.InsuranceRequiresProject);

        await using var db = _factory.CreateContext();
        (await db.ProductHoldings.AnyAsync(CancellationToken.None)).Should().BeFalse();
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task The_holding_remembers_whether_its_line_forbids_a_second(bool allowMultiple, bool singlePerLine)
    {
        // The unique index reads this snapshot: without it, it refused every second live contract,
        // including on a line that allows several (a won « Obsèques » project opened no draft).
        Line(allowMultipleHoldings: allowMultiple);

        (await SubscribeAsync()).IsSuccess.Should().BeTrue();

        await using var db = _factory.CreateContext();
        (await db.ProductHoldings.SingleAsync(CancellationToken.None)).SinglePerLine.Should().Be(singlePerLine);
    }

    [Fact]
    public async Task A_product_line_the_catalogue_does_not_have_is_refused()
    {
        _admin.GetBeneficiaryPolicyAsync(_tenant, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
              .Returns((BeneficiaryPolicyDto?)null);

        (await SubscribeAsync()).Error.Should().Be(CustomerErrors.ProductLineNotFound);
    }

    [Fact]
    public async Task An_unreachable_catalogue_refuses_the_subscription()
    {
        // Letting it through would store a holding with no category, no roles and no
        // multiple-holding rule — and the designation gate would then accept anything.
        _admin.GetBeneficiaryPolicyAsync(_tenant, _lineId, Arg.Any<CancellationToken>())
              .Returns<Task<BeneficiaryPolicyDto?>>(_ => throw new InvalidOperationException("M12 down"));

        (await SubscribeAsync()).Error.Should().Be(CustomerErrors.ProductLineUnavailable);
    }

    [Fact]
    public async Task A_line_that_forbids_multiple_holdings_refuses_the_second()
    {
        Line(allowMultipleHoldings: false);

        (await SubscribeAsync()).IsSuccess.Should().BeTrue();
        var second = await SubscribeAsync();

        second.IsFailure.Should().BeTrue();
        second.Error.Should().Be(CustomerErrors.HoldingAlreadyHeld,
            "one health policy per person — the rule lives in M12 and a partial index cannot see it");
    }

    [Fact]
    public async Task A_line_that_allows_them_accepts_a_second()
    {
        // A savings account, a tontine or a credit are legitimately held several times, which is
        // why AllowMultipleHoldings defaults to true.
        (await SubscribeAsync()).IsSuccess.Should().BeTrue();
        (await SubscribeAsync()).IsSuccess.Should().BeTrue();

        await using var db = _factory.CreateContext();
        (await db.ProductHoldings.CountAsync(CancellationToken.None)).Should().Be(2);
    }

    [Fact]
    public async Task A_closed_holding_does_not_block_a_fresh_subscription()
    {
        Line(allowMultipleHoldings: false);
        var first = (await SubscribeAsync()).Value;

        await using (var db = _factory.CreateContext())
        {
            var handler = new CloseHoldingHandler(Context(db));
            await handler.Handle(new CloseHoldingCommand(first, "Surrendered"), CancellationToken.None);
        }

        (await SubscribeAsync()).IsSuccess.Should().BeTrue(
            "the ceiling is on LIVE holdings, not on the history");
    }

    [Fact]
    public async Task An_invalid_clause_refuses_the_whole_subscription()
    {
        // Rather than leaving a holding behind with no beneficiaries for somebody to discover.
        var result = await SubscribeAsync([Beneficiary(60m)]);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.DesignationSharesMustTotal100);

        await using var db = _factory.CreateContext();
        (await db.ProductHoldings.CountAsync(CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task The_clause_may_travel_with_the_subscription()
    {
        var result = await SubscribeAsync([Beneficiary(100m)]);

        result.IsSuccess.Should().BeTrue(result.Error);

        await using var db = _factory.CreateContext();
        var designation = await db.BeneficiaryDesignations.SingleAsync(CancellationToken.None);

        designation.SharePercent.Should().Be(100m);
        designation.ClauseVersion.Should().Be(1);
    }

    // ── designating on a LOADED holding ─────────────────────────────────────

    [Fact]
    public async Task A_clause_recorded_on_a_reloaded_holding_is_inserted_and_not_updated()
    {
        // THE trap this module has paid for before: EF treats an app-assigned Guid key as
        // store-generated, so a child appended to a LOADED collection is tracked Modified and
        // SaveChanges attempts an UPDATE on a row that was never inserted. The aggregate tests
        // cannot catch it — they add children to a NEW parent, where the whole graph is Added.
        var holdingId = (await SubscribeAsync()).Value;

        var result = await DesignateAsync(holdingId, [Beneficiary(100m)]);

        result.IsSuccess.Should().BeTrue(result.Error);

        await using var db = _factory.CreateContext();
        (await db.BeneficiaryDesignations.CountAsync(CancellationToken.None)).Should().Be(1);
    }

    [Fact]
    public async Task Replacing_a_draft_clause_leaves_one_current_designation()
    {
        var holdingId = (await SubscribeAsync([Beneficiary(100m, "Awa")])).Value;

        await DesignateAsync(holdingId, [Beneficiary(100m, "Kofi")]);

        await using var db = _factory.CreateContext();
        var designations = await db.BeneficiaryDesignations
            .Where(d => d.HoldingId == holdingId)
            .ToListAsync(CancellationToken.None);

        designations.Where(d => d.ValidTo == null).Should().ContainSingle()
                    .Which.ExternalFullName.Should().Be("Kofi");

        designations.Should().HaveCount(2, "the replaced one is closed, not deleted");
    }

    [Fact]
    public async Task The_clause_change_is_announced_with_ids_and_a_version_only()
    {
        var holdingId = (await SubscribeAsync()).Value;
        _publisher.ClearReceivedCalls();

        await DesignateAsync(holdingId, [Beneficiary(100m, "Awa Ouattara")]);

        var evt = Published<BeneficiaryDesignationChangedEvent>();
        evt.HoldingId.Should().Be(holdingId);
        evt.ClauseVersion.Should().Be(1);
        evt.DesignationCount.Should().Be(1);
        evt.IsAmendment.Should().BeFalse("the holding is still a draft");

        // Who receives a capital is not something an outbox row should carry.
        System.Text.Json.JsonSerializer.Serialize(evt).Should().NotContain("Ouattara");
    }

    [Fact]
    public async Task A_change_to_a_live_clause_is_announced_as_an_amendment()
    {
        var holdingId = (await SubscribeAsync([Beneficiary(100m)])).Value;
        await WalkToActiveAsync(holdingId);
        _publisher.ClearReceivedCalls();

        var result = await DesignateAsync(holdingId, [Beneficiary(100m, "Kofi")]);

        result.Value.ClauseVersion.Should().Be(2);
        result.Value.IsAmendment.Should().BeTrue();

        Published<BeneficiaryDesignationChangedEvent>().IsAmendment.Should().BeTrue();
    }

    // ── the signature gate, through the handlers ────────────────────────────

    [Fact]
    public async Task A_missing_required_role_blocks_the_signature_request()
    {
        var holdingId = (await SubscribeAsync()).Value;

        await using var db = _factory.CreateContext();
        var handler = new RequestSignatureHandler(Context(db));

        var result = await handler.Handle(new RequestSignatureCommand(holdingId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.DesignationRequiredRoleMissing,
            "the line's policy demands one beneficiary, and the clause is empty");
    }

    [Fact]
    public async Task An_agent_with_no_professional_card_cannot_sign_an_insurance_subscription()
    {
        // CIMA articles 503 and 510. Checked at the SIGNATURE, which is the act of presenting the
        // operation — not only at the submission, because that is a separate act days later.
        var holdingId = (await SubscribeAsync([Beneficiary(100m)])).Value;

        await using (var db = _factory.CreateContext())
        {
            await new RequestSignatureHandler(Context(db))
                .Handle(new RequestSignatureCommand(holdingId), CancellationToken.None);
        }

        await using (var db = _factory.CreateContext())
        {
            var result = await new SignHoldingHandler(
                    Context(db), TestDistributionGuard.InsuranceWithoutLicence())
                .Handle(new SignHoldingCommand(holdingId, "doc://signature/1"), CancellationToken.None);

            result.IsFailure.Should().BeTrue();
            result.Error.Should().Be(InsuranceDistributionGuard.CredentialRequired);
        }

        // And nothing was recorded: a refused signature must not leave a signature reference
        // behind for the submission to find.
        await using (var db = _factory.CreateContext())
        {
            var holding = await db.ProductHoldings.FirstAsync(h => h.Id == holdingId);
            holding.SignedAt.Should().BeNull();
            holding.SignatureReference.Should().BeNull();
        }
    }

    private static Sankore.Modules.Integration.PublicApi.IIntegrationModule SheetDelivered(bool delivered)
    {
        var integration = Substitute.For<Sankore.Modules.Integration.PublicApi.IIntegrationModule>();
        integration.Insurance.HasInformationSheetDeliveryAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(delivered);
        return integration;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_insurance_contract_is_signed_only_after_the_information_sheet(bool delivered)
    {
        // Insurance lot 2: no signature of a proposal without the sheet handed to the client.
        var holdingId = (await SubscribeAsync([Beneficiary(100m)])).Value;

        await using (var db = _factory.CreateContext())
        {
            await new RequestSignatureHandler(Context(db))
                .Handle(new RequestSignatureCommand(holdingId), CancellationToken.None);
        }

        await using (var db = _factory.CreateContext())
        {
            var result = await new SignHoldingHandler(
                    Context(db), TestDistributionGuard.InsuranceWithLicence(), SheetDelivered(delivered))
                .Handle(new SignHoldingCommand(holdingId, "doc://signature/1"), CancellationToken.None);

            if (delivered)
                result.IsSuccess.Should().BeTrue(result.Error);
            else
                result.Error.Should().Be(CustomerErrors.InformationSheetNotDelivered);
        }
    }

    [Fact]
    public async Task A_card_withdrawn_between_the_signature_and_the_submission_stops_the_send()
    {
        // The reason the submission re-checks although the signature already required it: the two
        // are separate acts, and this is the last transition before a contract leaves the
        // institution. Same argument BNF-B-16 makes one step further down the chain.
        var holdingId = (await SubscribeAsync([Beneficiary(100m)])).Value;

        await using (var db = _factory.CreateContext())
        {
            await new RequestSignatureHandler(Context(db))
                .Handle(new RequestSignatureCommand(holdingId), CancellationToken.None);
        }

        await using (var db = _factory.CreateContext())
        {
            // Licensed at the time of signing, the information sheet handed over.
            var signed = await new SignHoldingHandler(Context(db), TestDistributionGuard.InsuranceWithLicence(), SheetDelivered(true))
                .Handle(new SignHoldingCommand(holdingId, "doc://signature/1"), CancellationToken.None);
            signed.IsSuccess.Should().BeTrue(signed.Error);
        }

        _publisher.ClearReceivedCalls();

        await using (var db = _factory.CreateContext())
        {
            // The card is gone by the time the file is submitted.
            var result = await new SubmitHoldingHandler(
                    Context(db), TestDistributionGuard.InsuranceWithoutLicence(), _publisher)
                .Handle(new SubmitHoldingCommand(holdingId), CancellationToken.None);

            result.IsFailure.Should().BeTrue();
            result.Error.Should().Be(InsuranceDistributionGuard.CredentialRequired);
        }

        await _publisher.DidNotReceiveWithAnyArgs()
            .PublishAsync(Arg.Any<ProductSubscriptionRequestedEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Submitting_a_signed_holding_announces_the_subscription()
    {
        var holdingId = (await SubscribeAsync([Beneficiary(100m)])).Value;

        await using (var db = _factory.CreateContext())
        {
            await new RequestSignatureHandler(Context(db))
                .Handle(new RequestSignatureCommand(holdingId), CancellationToken.None);
        }

        await using (var db = _factory.CreateContext())
        {
            await new SignHoldingHandler(Context(db), TestDistributionGuard.AllowAll())
                .Handle(new SignHoldingCommand(holdingId, "doc://signature/1"), CancellationToken.None);
        }

        _publisher.ClearReceivedCalls();

        await using (var db = _factory.CreateContext())
        {
            var result = await new SubmitHoldingHandler(Context(db), TestDistributionGuard.AllowAll(), _publisher)
                .Handle(new SubmitHoldingCommand(holdingId), CancellationToken.None);

            result.IsSuccess.Should().BeTrue(result.Error);
        }

        var evt = Published<ProductSubscriptionRequestedEvent>();
        evt.HoldingId.Should().Be(holdingId);
        evt.ProductLineId.Should().Be(_lineId);
        evt.ClauseVersion.Should().Be(1);
        evt.RequestedBy.Should().Be(_actor);
    }

    [Fact]
    public async Task An_unsigned_holding_announces_nothing()
    {
        var holdingId = (await SubscribeAsync([Beneficiary(100m)])).Value;

        await using (var db = _factory.CreateContext())
        {
            await new RequestSignatureHandler(Context(db))
                .Handle(new RequestSignatureCommand(holdingId), CancellationToken.None);
        }

        _publisher.ClearReceivedCalls();

        await using (var db = _factory.CreateContext())
        {
            var result = await new SubmitHoldingHandler(Context(db), TestDistributionGuard.AllowAll(), _publisher)
                .Handle(new SubmitHoldingCommand(holdingId), CancellationToken.None);

            result.Error.Should().Be(CustomerErrors.SignatureRequired);
        }

        _publisher.ReceivedCalls().Should().BeEmpty(
            "nothing reaches a back office, and nothing claims it did");
    }

    // ── reads and perimeter ─────────────────────────────────────────────────

    [Fact]
    public async Task The_list_filters_by_category_and_status_and_pages()
    {
        await SubscribeAsync();
        await SubscribeAsync();

        await using var db = _factory.CreateContext();
        var handler = new ListHoldingsHandler(
            db, TestDoubles.CurrentUser(_tenant, _actor), TestDoubles.AgencyScope(),
            new SubjectLabelResolver(db));

        var all = await handler.Handle(
            new ListHoldingsQuery(_clientId, null, null, 1, 20), CancellationToken.None);

        var byStatus = await handler.Handle(
            new ListHoldingsQuery(_clientId, null, nameof(HoldingStatus.Active), 1, 20),
            CancellationToken.None);

        var byCategory = await handler.Handle(
            new ListHoldingsQuery(_clientId, nameof(ProductCategory.Loan), null, 1, 20),
            CancellationToken.None);

        var firstPage = await handler.Handle(
            new ListHoldingsQuery(_clientId, null, null, Page: 1, PageSize: 1), CancellationToken.None);

        all.Value.TotalCount.Should().Be(2);
        byStatus.Value.TotalCount.Should().Be(0, "both are drafts");
        byCategory.Value.TotalCount.Should().Be(0, "both are savings");
        firstPage.Value.Items.Should().HaveCount(1);
        firstPage.Value.TotalCount.Should().Be(2);
    }

    [Fact]
    public async Task A_negative_page_does_not_produce_a_negative_skip()
    {
        await SubscribeAsync();

        await using var db = _factory.CreateContext();
        var handler = new ListHoldingsHandler(
            db, TestDoubles.CurrentUser(_tenant, _actor), TestDoubles.AgencyScope(),
            new SubjectLabelResolver(db));

        var result = await handler.Handle(
            new ListHoldingsQuery(_clientId, null, null, Page: -3, PageSize: 20),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Page.Should().Be(1);
        result.Value.Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task An_unparseable_status_filter_is_refused_rather_than_ignored()
    {
        await using var db = _factory.CreateContext();
        var handler = new ListHoldingsHandler(
            db, TestDoubles.CurrentUser(_tenant, _actor), TestDoubles.AgencyScope(),
            new SubjectLabelResolver(db));

        var result = await handler.Handle(
            new ListHoldingsQuery(_clientId, null, "Pendingg", 1, 20), CancellationToken.None);

        result.IsFailure.Should().BeTrue("silently returning every row would look like a filter that works");
    }

    [Fact]
    public async Task A_holding_outside_the_callers_perimeter_is_not_found()
    {
        var holdingId = (await SubscribeAsync()).Value;

        await using var db = _factory.CreateContext();
        var handler = new GetHoldingHandler(
            db,
            TestDoubles.CurrentUser(_tenant, _actor),
            // A perimeter that contains some OTHER agency.
            TestDoubles.AgencyScope(Guid.NewGuid()),
            new SubjectLabelResolver(db));

        var result = await handler.Handle(new GetHoldingQuery(holdingId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.HoldingNotFound,
            "never 403 — the existence of a holding must not leak either");
    }

    [Fact]
    public async Task The_detail_resolves_a_designated_subjects_name()
    {
        var holdingId = (await SubscribeAsync()).Value;

        // A designation pointing at the client themselves — the one subject a test can resolve
        // without seeding a relationship.
        await DesignateAsync(holdingId,
        [
            new DesignationInput(
                RoleCode: "BENEFICIAIREDECES",
                SubjectType: nameof(DesignationSubjectType.Client),
                SubjectId: _clientId,
                Rank: 1,
                SharePercent: 100m),
        ]);

        await using var db = _factory.CreateContext();
        var handler = new GetHoldingHandler(
            db, TestDoubles.CurrentUser(_tenant, _actor), TestDoubles.AgencyScope(),
            new SubjectLabelResolver(db));

        var result = await handler.Handle(new GetHoldingQuery(holdingId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Designations.Should().ContainSingle()
              .Which.SubjectLabel.Should().NotBeNullOrWhiteSpace();
        result.Value.CanEdit.Should().BeTrue("a draft is editable");
    }

    /// <summary>Takes a holding from draft to Active the way the agent and the back office would.</summary>
    private async Task WalkToActiveAsync(Guid holdingId)
    {
        await using (var db = _factory.CreateContext())
        {
            await new RequestSignatureHandler(Context(db))
                .Handle(new RequestSignatureCommand(holdingId), CancellationToken.None);
        }

        await using (var db = _factory.CreateContext())
        {
            await new SignHoldingHandler(Context(db), TestDistributionGuard.AllowAll())
                .Handle(new SignHoldingCommand(holdingId, "doc://signature/1"), CancellationToken.None);
        }

        await using (var db = _factory.CreateContext())
        {
            await new SubmitHoldingHandler(Context(db), TestDistributionGuard.AllowAll(), _publisher)
                .Handle(new SubmitHoldingCommand(holdingId), CancellationToken.None);
        }

        // The back office answering — an integration event in production, called directly here.
        await using (var db = _factory.CreateContext())
        {
            var holding = await db.ProductHoldings
                .AsTracking()
                .Include(h => h.Designations)
                .SingleAsync(h => h.Id == holdingId, CancellationToken.None);

            holding.ConfirmSync("Orass", "POL-1", DateTimeOffset.UtcNow).IsSuccess.Should().BeTrue();
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }
}
