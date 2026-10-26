namespace Sankore.Modules.Customers.Tests.Features.Holdings;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Holdings.ListContracts;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The client's contracts as one list (product owner, 2026-10-26): holdings, with the policy or
/// account that realises each folded in, plus what the back offices hold and no holding accounts for.
/// </summary>
public sealed class ListClientContractsHandlerTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _agency = Guid.NewGuid();
    private readonly Guid _actor = Guid.NewGuid();
    private readonly TestCustomersDbContextFactory _factory;
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();
    private readonly IIntegrationModule _integration = Substitute.For<IIntegrationModule>();
    private readonly IInsuranceGateway _insurance = Substitute.For<IInsuranceGateway>();
    private readonly ICoreBankingGateway _coreBanking = Substitute.For<ICoreBankingGateway>();
    private readonly Guid _clientId;

    private static readonly string[] EveryReadPermission =
    [
        Permissions.CanViewInsurancePolicy.Code,
        Permissions.CanViewLiveBalance.Code,
    ];

    public ListClientContractsHandlerTests()
    {
        _factory = new TestCustomersDbContextFactory(_tenant);

        using var db = _factory.CreateContext();
        var client = TestClientFactory.Individual(_tenant, _agency);
        db.Clients.Add(client);
        db.SaveChanges();
        _clientId = client.Id;

        _integration.Insurance.Returns(_insurance);
        _integration.CoreBanking.Returns(_coreBanking);
        _insurance.ListRecordedPoliciesAsync(_clientId, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<RecordedPolicy>());

        _admin.ListProductsAsync(_tenant, null, Arg.Any<CancellationToken>())
            .Returns([new ProductSummary("VIE-01", "Assurance famille", nameof(ProductCategory.Insurance), true)]);
    }

    public void Dispose() => _factory.Dispose();

    // ── fixtures ────────────────────────────────────────────────────────────

    private async Task<Result<ClientContractsDto>> ListAsync(
        string[]? permissions = null, ITenantModules? modules = null)
    {
        await using var db = _factory.CreateContext();

        var user = TestDoubles.CurrentUser(_tenant, _actor);
        user.Permissions.Returns(permissions ?? EveryReadPermission);

        var handler = new ListClientContractsHandler(
            db, user, TestDoubles.AgencyScope(), _admin,
            modules ?? FixedTenantModules.All,
            NullLogger<ListClientContractsHandler>.Instance,
            _integration);

        return await handler.Handle(new ListClientContractsQuery(_clientId), CancellationToken.None);
    }

    /// <summary>A holding, taken to Active with a back office reference when one is given.</summary>
    private async Task<Guid> HoldingAsync(
        string category = nameof(ProductCategory.Insurance), string code = "VIE-01", string? externalRef = null)
    {
        await using var db = _factory.CreateContext();
        var now = DateTimeOffset.UtcNow;

        var holding = TestHoldingFactory.Draft(
            _tenant, _clientId, TestHoldingFactory.Line(code: code, category: category),
            specifics: new HoldingSpecifics(Premium: 12_000m, Currency: "XOF"));

        if (externalRef is not null)
        {
            holding.RequestSignature([], now).IsSuccess.Should().BeTrue();
            holding.Sign("SIG-1", now, now).IsSuccess.Should().BeTrue();
            holding.SubmitForSync(now).IsSuccess.Should().BeTrue();
            holding.ConfirmSync("CBS", externalRef, now).IsSuccess.Should().BeTrue();
        }

        db.ProductHoldings.Add(holding);
        await db.SaveChangesAsync();
        return holding.Id;
    }

    private static RecordedPolicy Policy(Guid? holdingId, string number = "POL-1") => new(
        PolicyId: Guid.NewGuid(),
        PolicyNumber: number,
        Insurer: "Assureur Vie",
        ProductId: null,
        ProductName: "Famille Sérénité",
        Status: PolicyStatus.Issued,
        StatusDetail: null,
        EffectiveDate: new DateOnly(2026, 10, 1),
        ExpiryDate: null,
        NextDueDate: null,
        PremiumAmount: 15_000m,
        Currency: "XOF",
        Periodicity: PremiumPeriodicity.Monthly,
        InsuredAmount: null,
        HoldingId: holdingId,
        LastSyncedAt: DateTimeOffset.UtcNow,
        PremiumOutstanding: true);

    private void Snapshot(IReadOnlyList<CbsAccount>? accounts = null, IReadOnlyList<CbsLoan>? loans = null)
        => _coreBanking.GetCustomerSnapshotAsync(_clientId, Arg.Any<CancellationToken>())
            .Returns(new CbsCustomerSnapshot(
                _clientId, accounts ?? [], loans ?? [], 0m, 0m, null, new DateTimeOffset(2026, 10, 25, 6, 0, 0, TimeSpan.Zero)));

    private static CbsAccount Account(string id, string number) => new(
        new ExternalId(id), number, "EPARGNE", "Épargne Plus", "XOF", 250_000m, null, "Open", new DateOnly(2024, 1, 5));

    // ── folding ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_policy_issued_from_a_holding_is_folded_into_it_not_listed_twice()
    {
        var holdingId = await HoldingAsync();
        var policy = Policy(holdingId);
        _insurance.ListRecordedPoliciesAsync(_clientId, Arg.Any<CancellationToken>()).Returns([policy]);

        var line = (await ListAsync()).Value.Items.Should().ContainSingle().Subject;

        line.Key.Should().Be($"holding:{holdingId}");
        line.Source.Should().Be("Crm");
        line.Nature.Should().Be("Insurance");
        line.Label.Should().Be("Assurance famille", "the catalogue's name, not the code");
        line.PolicyId.Should().Be(policy.PolicyId);
        line.Reference.Should().Be("POL-1");
        line.ExternalStatus.Should().Be(nameof(PolicyStatus.Issued));
        line.Amount.Should().Be(15_000m, "the insurer's premium, once there is a policy");
        line.AmountKind.Should().Be("Premium");
        line.PremiumOutstanding.Should().BeTrue();
    }

    [Fact]
    public async Task A_policy_no_holding_accounts_for_is_listed_on_its_own()
    {
        await HoldingAsync();
        var foreign = Policy(holdingId: null, number: "POL-AGENCE");
        _insurance.ListRecordedPoliciesAsync(_clientId, Arg.Any<CancellationToken>()).Returns([foreign]);

        var items = (await ListAsync()).Value.Items;

        items.Should().HaveCount(2);
        var line = items.Single(i => i.Source == "Insurer");
        line.Key.Should().Be($"policy:{foreign.PolicyId}");
        line.HoldingId.Should().BeNull();
        line.Label.Should().Be("Famille Sérénité");
        line.Insurer.Should().Be("Assureur Vie");
    }

    [Fact]
    public async Task An_account_is_folded_into_the_holding_whose_reference_it_carries()
    {
        var holdingId = await HoldingAsync(nameof(ProductCategory.Savings), "EPARGNE", externalRef: "ACC-77");
        Snapshot(accounts: [Account("ACC-77", "001-77"), Account("ACC-99", "001-99")]);

        var result = (await ListAsync()).Value;

        result.Items.Should().HaveCount(2);
        var mine = result.Items.Single(i => i.HoldingId == holdingId);
        mine.Nature.Should().Be("Account");
        mine.AccountRef.Should().Be("ACC-77");
        mine.Reference.Should().Be("001-77");
        mine.Amount.Should().Be(250_000m);
        mine.AmountKind.Should().Be("Balance");

        var other = result.Items.Single(i => i.Source == "CoreBanking");
        other.Key.Should().Be("account:ACC-99");
        other.Label.Should().Be("Épargne Plus");
        result.CoreBankingAsOf.Should().Be(new DateTimeOffset(2026, 10, 25, 6, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task A_loan_the_bank_holds_alone_is_listed_with_its_outstanding()
    {
        Snapshot(loans: [new CbsLoan(new ExternalId("LN-5"), "CREDIT", 500_000m, 320_000m, "XOF", "Active", null, null, null)]);

        var line = (await ListAsync()).Value.Items.Should().ContainSingle().Subject;

        line.Key.Should().Be("loan:LN-5");
        line.Nature.Should().Be("Loan");
        line.Amount.Should().Be(320_000m);
        line.AmountKind.Should().Be("Outstanding");
    }

    [Fact]
    public async Task A_holding_with_no_back_office_shows_the_terms_the_agent_typed()
    {
        await HoldingAsync();

        var line = (await ListAsync()).Value.Items.Should().ContainSingle().Subject;

        line.Status.Should().Be(nameof(HoldingStatus.Draft));
        line.Amount.Should().Be(12_000m);
        line.AmountKind.Should().Be("Premium");
        line.ExternalStatus.Should().BeNull();
    }

    [Fact]
    public async Task A_designation_left_by_a_household_change_is_counted_on_its_line()
    {
        var member = Guid.NewGuid();
        await using (var db = _factory.CreateContext())
        {
            var holding = TestHoldingFactory.Draft(_tenant, _clientId, TestHoldingFactory.Line());
            holding.Designate(
                    [TestHoldingFactory.Beneficiary(100m, subjectType: DesignationSubjectType.HouseholdMember, subjectId: member)],
                    [TestHoldingFactory.DeathBenefit], _actor, DateTimeOffset.UtcNow)
                .IsSuccess.Should().BeTrue();
            holding.FlagDesignationsOf(member, "Sorti du foyer").Should().Be(1);
            db.ProductHoldings.Add(holding);
            await db.SaveChangesAsync();
        }

        (await ListAsync()).Value.Items.Single().DesignationsToReview.Should().Be(1);
    }

    // ── who reads what ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_caller_without_the_policy_permission_does_not_get_the_insurer_s_lines()
    {
        _insurance.ListRecordedPoliciesAsync(_clientId, Arg.Any<CancellationToken>()).Returns([Policy(null)]);

        var result = await ListAsync(permissions: [Permissions.CanViewLiveBalance.Code]);

        result.Value.Items.Should().BeEmpty();
        await _insurance.DidNotReceiveWithAnyArgs().ListRecordedPoliciesAsync(default, default);
    }

    [Fact]
    public async Task A_tenant_that_never_had_the_module_reads_nothing_from_its_back_office()
    {
        _insurance.ListRecordedPoliciesAsync(_clientId, Arg.Any<CancellationToken>()).Returns([Policy(null)]);
        Snapshot(accounts: [Account("ACC-1", "001")]);

        var result = await ListAsync(modules: FixedTenantModules.None);

        result.Value.Items.Should().BeEmpty();
        await _insurance.DidNotReceiveWithAnyArgs().ListRecordedPoliciesAsync(default, default);
        await _coreBanking.DidNotReceiveWithAnyArgs().GetCustomerSnapshotAsync(default, default);
    }

    [Fact]
    public async Task A_deactivated_module_is_still_read_for_consultation()
    {
        _insurance.ListRecordedPoliciesAsync(_clientId, Arg.Any<CancellationToken>()).Returns([Policy(null)]);

        var result = await ListAsync(modules: new FixedTenantModules { ReadOnly = [TenantModules.Insurance] });

        result.Value.Items.Should().ContainSingle(i => i.Source == "Insurer");
    }

    [Fact]
    public async Task An_insurer_outage_keeps_the_holdings_and_says_the_list_is_partial()
    {
        await HoldingAsync();
        _insurance.ListRecordedPoliciesAsync(_clientId, Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<RecordedPolicy>>>(_ => throw new InvalidOperationException("down"));

        var result = (await ListAsync()).Value;

        result.Items.Should().ContainSingle(i => i.Source == "Crm");
        result.UnavailableSources.Should().Equal("Insurer");
    }

    [Fact]
    public async Task An_unreachable_catalogue_shows_the_product_code_rather_than_failing()
    {
        await HoldingAsync();
        _admin.ListProductsAsync(_tenant, null, Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<ProductSummary>>>(_ => throw new InvalidOperationException("M12 down"));

        (await ListAsync()).Value.Items.Single().Label.Should().Be("VIE-01");
    }

    [Fact]
    public async Task A_client_outside_the_perimeter_reads_as_not_found()
    {
        await using var db = _factory.CreateContext();
        var handler = new ListClientContractsHandler(
            db, TestDoubles.CurrentUser(_tenant, _actor), TestDoubles.AgencyScope(Guid.NewGuid()), _admin,
            FixedTenantModules.All, NullLogger<ListClientContractsHandler>.Instance, _integration);

        var result = await handler.Handle(new ListClientContractsQuery(_clientId), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientNotFound);
    }

    [Theory]
    [InlineData(nameof(ProductCategory.HealthInsurance), "Insurance")]
    [InlineData(nameof(ProductCategory.Agriculture), "Loan")]
    [InlineData(nameof(ProductCategory.Tontine), "Account")]
    [InlineData(nameof(ProductCategory.Standard), "Other")]
    [InlineData("Inconnue", "Other")]
    public void The_nature_follows_the_category_snapshot(string category, string nature)
        => ListClientContractsHandler.NatureOf(category).Should().Be(nature);
}
