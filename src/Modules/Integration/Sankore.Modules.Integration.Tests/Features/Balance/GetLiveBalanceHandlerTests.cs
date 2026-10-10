namespace Sankore.Modules.Integration.Tests.Features.Balance;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.Features.Balance;
using Sankore.Modules.Integration.Features.Balance.GetLiveBalance;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.Features.Commands;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel.Authorization;
using Xunit;

/// <summary>
/// INT-15 criterion 3: the read is restricted to the clients of the agent's own agency, and a
/// client outside that perimeter is answered NOT FOUND — never FORBIDDEN.
/// </summary>
public sealed class GetLiveBalanceHandlerTests
{
    private static readonly Guid TenantId = BalanceTestHarness.TenantId;

    private static readonly Guid CrmCustomerId = BalanceTestHarness.CrmCustomerId;

    private static readonly Guid OwnAgency = new("11111111-0000-0000-0000-00000000000f");

    private static readonly Guid OtherAgency = new("22222222-0000-0000-0000-00000000000f");

    [Fact]
    public async Task A_client_of_the_agents_own_agency_gets_the_balance()
    {
        var gateway = Gateway(BalanceTestHarness.LiveBalance(balance: 125_000m));
        var handler = Handler(gateway, OwnAgency, accessible: OwnAgency);

        var result = await handler.Handle(
            new GetLiveBalanceQuery(CrmCustomerId, BalanceTestHarness.AccountId), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Balance.Should().Be(125_000m);
        result.Value.Currency.Should().Be("XOF");
        result.Value.AccountId.Should().Be(BalanceTestHarness.AccountId);
        result.Value.IsStale.Should().BeFalse();
    }

    [Fact]
    public async Task A_client_outside_the_perimeter_answers_not_found_and_never_forbidden()
    {
        var gateway = Gateway(BalanceTestHarness.LiveBalance());
        var handler = Handler(gateway, OtherAgency, accessible: OwnAgency);

        var result = await handler.Handle(
            new GetLiveBalanceQuery(CrmCustomerId, BalanceTestHarness.AccountId), default);

        result.IsFailure.Should().BeTrue();

        // The SAME code an unknown client gets. A distinct "forbidden" answer would confirm that
        // this customer exists in another branch, and a clerk could enumerate another branch's
        // book one id at a time — which is the one thing the perimeter exists to prevent.
        result.Error.Should().Be(BalanceErrors.BalanceNotAvailable);

        // And nothing was asked of the core banking system on their behalf.
        await gateway.DidNotReceive().GetLiveBalanceAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unknown_client_answers_the_same_code_as_one_outside_the_perimeter()
    {
        var gateway = Gateway(BalanceTestHarness.LiveBalance());

        var customers = Substitute.For<ICustomersModule>();
        customers.GetClientSummaryAsync(TenantId, CrmCustomerId, Arg.Any<CancellationToken>())
            .Returns((ClientSummary?)null);

        var handler = new GetLiveBalanceHandler(
            Module(gateway), customers, Scope(OwnAgency), CommandsTestHarness.User(TenantId));

        var result = await handler.Handle(
            new GetLiveBalanceQuery(CrmCustomerId, BalanceTestHarness.AccountId), default);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(BalanceErrors.BalanceNotAvailable);

        await gateway.DidNotReceive().GetLiveBalanceAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_stale_figure_reaches_the_caller_flagged_and_dated()
    {
        var asOf = CommandsTestHarness.Now.AddHours(-9);

        var gateway = Substitute.For<ICoreBankingGateway>();
        gateway.GetLiveBalanceAsync(CrmCustomerId, BalanceTestHarness.AccountId, Arg.Any<CancellationToken>())
            .Returns(new CbsBalance(
                AccountId: new ExternalId(BalanceTestHarness.AccountId),
                Currency: "XOF",
                Balance: 75_000m,
                AvailableBalance: 75_000m,
                AsOf: asOf,
                IsStale: true));

        var handler = Handler(gateway, OwnAgency, accessible: OwnAgency);

        var result = await handler.Handle(
            new GetLiveBalanceQuery(CrmCustomerId, BalanceTestHarness.AccountId), default);

        result.IsSuccess.Should().BeTrue();

        // Both halves travel, because a clerk shown a stale figure without its date will quote it
        // to the customer as if it were current.
        result.Value.IsStale.Should().BeTrue();
        result.Value.AsOf.Should().Be(asOf);
    }

    [Fact]
    public async Task An_account_the_facade_refuses_answers_not_found()
    {
        // The facade's own ownership check (§5bis(c)) answers null for an account that is not this
        // customer's. The handler must not turn that into anything else.
        var gateway = Substitute.For<ICoreBankingGateway>();
        gateway.GetLiveBalanceAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((CbsBalance?)null);

        var handler = Handler(gateway, OwnAgency, accessible: OwnAgency);

        var result = await handler.Handle(
            new GetLiveBalanceQuery(CrmCustomerId, "CBS-ACC-9999"), default);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(BalanceErrors.BalanceNotAvailable);
    }

    // ── Wiring ──────────────────────────────────────────────────────────────

    private static ICoreBankingGateway Gateway(CbsBalance balance)
    {
        var gateway = Substitute.For<ICoreBankingGateway>();

        gateway.GetLiveBalanceAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(balance);

        return gateway;
    }

    private static IIntegrationModule Module(ICoreBankingGateway gateway)
    {
        var module = Substitute.For<IIntegrationModule>();
        module.CoreBanking.Returns(gateway);
        return module;
    }

    /// <summary>
    /// A perimeter holding exactly <paramref name="accessible"/>. <c>CanAccessAgencyAsync</c> is
    /// the method the handler calls, and it is the one an unrestricted super-user gets <c>true</c>
    /// from for every agency — which is why the handler never interprets a set itself.
    /// </summary>
    private static IAgencyScopeProvider Scope(params Guid[] accessible)
    {
        var scope = Substitute.For<IAgencyScopeProvider>();

        scope.CanAccessAgencyAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => accessible.Contains(call.ArgAt<Guid>(2)));

        return scope;
    }

    private static ICurrentUser User() => CommandsTestHarness.User(TenantId);

    private static GetLiveBalanceHandler Handler(
        ICoreBankingGateway gateway, Guid clientAgency, params Guid[] accessible)
    {
        var customers = Substitute.For<ICustomersModule>();

        customers.GetClientSummaryAsync(TenantId, CrmCustomerId, Arg.Any<CancellationToken>())
            .Returns(new ClientSummary(
                Id: CrmCustomerId,
                ClientNumber: "CRM-000123",
                ClientType: "Individual",
                DisplayName: "AWA OUATTARA",
                Status: "Active",
                AgencyId: clientAgency,
                AdvisorUserId: null,
                KycStatus: "Approved",
                RiskLevel: "Low",
                MergedIntoId: null));

        return new GetLiveBalanceHandler(Module(gateway), customers, Scope(accessible), User());
    }
}
