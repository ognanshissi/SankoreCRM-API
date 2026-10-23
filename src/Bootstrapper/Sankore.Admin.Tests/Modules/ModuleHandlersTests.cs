namespace Sankore.Admin.Tests.Modules;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Admin.Domain;
using Sankore.Admin.Features.Modules;
using Sankore.Admin.Features.Modules.PlatformSide;
using Sankore.Admin.Features.Modules.TenantSide;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>The request → decision → deactivation circuit, as the handlers enforce it.</summary>
public sealed class ModuleHandlersTests : IDisposable
{
    private readonly ModuleTestDb _store = new();
    private static readonly Guid User = Guid.NewGuid();
    private static readonly Guid Admin = Guid.NewGuid();

    public void Dispose() => _store.Dispose();

    private async Task<ModuleResult> RequestAsync(string module = TenantModules.Insurance,
        decimal price = ModuleTestDb.InsurancePrice, string currency = "XOF", Guid? tenantId = null)
    {
        await using var db = _store.Create();
        return await new RequestTenantModuleHandler(db, NullLogger<RequestTenantModuleHandler>.Instance)
            .Handle(new RequestTenantModuleCommand(tenantId ?? _store.TenantId, module, User, "admin@caisse.ci",
                price, currency), default);
    }

    private async Task<ModuleResult> DecideAsync(Guid subscriptionId, bool approve, string? reason = null)
    {
        await using var db = _store.Create();
        return await new DecideModuleRequestHandler(db, NullLogger<DecideModuleRequestHandler>.Instance)
            .Handle(new DecideModuleRequestCommand(subscriptionId, Admin, approve, reason), default);
    }

    private async Task<ModuleResult> DeactivateAsync(string module = TenantModules.Insurance)
    {
        await using var db = _store.Create();
        return await new DeactivateTenantModuleHandler(db, NullLogger<DeactivateTenantModuleHandler>.Instance)
            .Handle(new DeactivateTenantModuleCommand(_store.TenantId, module, User, "admin@caisse.ci"), default);
    }

    private async Task<List<TenantModuleSubscription>> RowsAsync()
    {
        await using var db = _store.Create();
        return await db.TenantModuleSubscriptions.AsNoTracking().ToListAsync();
    }

    [Fact]
    public async Task A_request_at_the_current_price_is_recorded_as_pending()
    {
        var result = await RequestAsync();

        result.IsSuccess.Should().BeTrue();
        var row = (await RowsAsync()).Single();
        row.Id.Should().Be(result.SubscriptionId!.Value);
        row.Status.Should().Be(ModuleSubscriptionStatus.Requested);
        row.AcceptedMonthlyPrice.Should().Be(ModuleTestDb.InsurancePrice);
    }

    [Fact]
    public async Task A_request_at_a_price_the_user_was_not_shown_is_refused()
    {
        var result = await RequestAsync(price: 100000m);

        result.Error.Should().Be(ModuleErrors.PriceChanged);
        (await RowsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_module_nobody_offered_yet_cannot_be_requested()
    {
        await using (var db = _store.Create())
        {
            var definition = await db.ModuleDefinitions.SingleAsync(m => m.Code == TenantModules.Insurance);
            definition.Update(definition.Label, definition.Description, definition.MonthlyPrice, definition.Currency,
                isAvailable: false, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        (await RequestAsync()).Error.Should().Be(ModuleErrors.ModuleUnavailable);
    }

    [Fact]
    public async Task Unknown_module_and_unknown_tenant_are_reported_as_such()
    {
        (await RequestAsync(module: "Telephony")).Error.Should().Be(ModuleErrors.ModuleUnknown,
            "telephony is socle, never a module");
        (await RequestAsync(tenantId: Guid.NewGuid())).Error.Should().Be(ModuleErrors.TenantNotFound);
    }

    [Fact]
    public async Task A_second_request_while_one_is_open_is_a_conflict()
    {
        var first = await RequestAsync();

        (await RequestAsync()).Error.Should().Be(ModuleErrors.RequestPending);

        await DecideAsync(first.SubscriptionId!.Value, approve: true);
        (await RequestAsync()).Error.Should().Be(ModuleErrors.AlreadyActive);
    }

    [Fact]
    public async Task Approval_turns_the_module_on_and_the_tenant_reads_it_active()
    {
        var request = await RequestAsync();

        (await DecideAsync(request.SubscriptionId!.Value, approve: true)).IsSuccess.Should().BeTrue();

        await using var db = _store.Create();
        var view = await new ListTenantModulesHandler(db).Handle(new ListTenantModulesQuery(_store.TenantId), default);
        var insurance = view!.Modules.Single(m => m.Code == TenantModules.Insurance);
        insurance.State.Should().Be(TenantModuleState.Active);
        view.Modules.Single(m => m.Code == TenantModules.BankingMicrofinance).State
            .Should().Be(TenantModuleState.None);
        view.Modules.Select(m => m.Code).Should().Equal(TenantModules.All, "the page lists modules in a fixed order");
    }

    [Fact]
    public async Task A_refusal_without_a_reason_is_refused_and_a_decided_request_cannot_be_decided_again()
    {
        var request = await RequestAsync();
        var id = request.SubscriptionId!.Value;

        (await DecideAsync(id, approve: false, reason: "")).Error.Should().Be(ModuleErrors.RejectionReasonRequired);
        (await DecideAsync(id, approve: false, reason: "Pas de contrat")).IsSuccess.Should().BeTrue();
        (await DecideAsync(id, approve: true)).Error.Should().Be(ModuleErrors.RequestNotPending);
        (await DecideAsync(Guid.NewGuid(), approve: true)).Error.Should().Be(ModuleErrors.SubscriptionNotFound);
    }

    [Fact]
    public async Task After_a_refusal_the_tenant_may_ask_again_and_the_history_keeps_both_rows()
    {
        var first = await RequestAsync();
        await DecideAsync(first.SubscriptionId!.Value, approve: false, reason: "Pas de contrat");

        (await RequestAsync()).IsSuccess.Should().BeTrue();

        var rows = await RowsAsync();
        rows.Should().HaveCount(2);
        rows.Select(r => r.Status).Should().BeEquivalentTo(
            [ModuleSubscriptionStatus.Rejected, ModuleSubscriptionStatus.Requested]);
    }

    [Fact]
    public async Task A_tenant_cancels_only_its_own_pending_request()
    {
        var request = await RequestAsync();
        var id = request.SubscriptionId!.Value;

        await using (var db = _store.Create())
        {
            var handler = new CancelTenantModuleRequestHandler(db, NullLogger<CancelTenantModuleRequestHandler>.Instance);
            (await handler.Handle(new CancelTenantModuleRequestCommand(Guid.NewGuid(), id, User), default))
                .Error.Should().Be(ModuleErrors.SubscriptionNotFound, "another tenant's row reads as absent");
            (await handler.Handle(new CancelTenantModuleRequestCommand(_store.TenantId, id, User), default))
                .IsSuccess.Should().BeTrue();
            (await handler.Handle(new CancelTenantModuleRequestCommand(_store.TenantId, id, User), default))
                .Error.Should().Be(ModuleErrors.RequestNotPending);
        }

        (await RowsAsync()).Single().Status.Should().Be(ModuleSubscriptionStatus.Cancelled);
    }

    [Fact]
    public async Task Deactivation_is_immediate_needs_no_approval_and_keeps_the_row()
    {
        (await DeactivateAsync()).Error.Should().Be(ModuleErrors.NotActive);

        var request = await RequestAsync();
        await DecideAsync(request.SubscriptionId!.Value, approve: true);

        (await DeactivateAsync()).IsSuccess.Should().BeTrue();

        var row = (await RowsAsync()).Single();
        row.Status.Should().Be(ModuleSubscriptionStatus.Deactivated);
        row.DeactivatedByEmail.Should().Be("admin@caisse.ci");
        row.ActivatedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_direct_activation_approves_a_pending_request_instead_of_duplicating_it()
    {
        var request = await RequestAsync();

        await using (var db = _store.Create())
        {
            var handler = new ActivateTenantModuleHandler(db, NullLogger<ActivateTenantModuleHandler>.Instance);
            (await handler.Handle(new ActivateTenantModuleCommand(_store.TenantId, TenantModules.Insurance, Admin), default))
                .SubscriptionId.Should().Be(request.SubscriptionId);
            (await handler.Handle(new ActivateTenantModuleCommand(_store.TenantId, TenantModules.Insurance, Admin), default))
                .Error.Should().Be(ModuleErrors.AlreadyActive);
            (await handler.Handle(new ActivateTenantModuleCommand(_store.TenantId, TenantModules.BankingMicrofinance, Admin), default))
                .IsSuccess.Should().BeTrue("the platform activates without a request");
        }

        var rows = await RowsAsync();
        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(r => r.Status == ModuleSubscriptionStatus.Active);
        rows.Single(r => r.ModuleCode == TenantModules.BankingMicrofinance).Source
            .Should().Be(ModuleSubscriptionSource.Direct);
    }

    [Fact]
    public async Task A_price_change_applies_to_later_requests_and_leaves_accepted_prices_alone()
    {
        var request = await RequestAsync();

        await using (var db = _store.Create())
        {
            var handler = new UpdateModuleDefinitionHandler(db, NullLogger<UpdateModuleDefinitionHandler>.Instance);
            (await handler.Handle(new UpdateModuleDefinitionCommand(TenantModules.Insurance, "Assurance", "",
                    -1m, "XOF", true), default))
                .Error.Should().Be(ModuleErrors.InvalidDefinition);
            (await handler.Handle(new UpdateModuleDefinitionCommand(TenantModules.Insurance, "Assurance", "",
                    200000m, "XO", true), default))
                .Error.Should().Be(ModuleErrors.InvalidDefinition);
            (await handler.Handle(new UpdateModuleDefinitionCommand(TenantModules.Insurance, "Assurance", "",
                    200000m, "xof", true), default))
                .IsSuccess.Should().BeTrue();
        }

        (await RowsAsync()).Single(r => r.Id == request.SubscriptionId).AcceptedMonthlyPrice
            .Should().Be(ModuleTestDb.InsurancePrice);
    }

    [Fact]
    public async Task The_platform_queue_lists_pending_requests_oldest_first_with_names()
    {
        await RequestAsync(TenantModules.Insurance);
        await RequestAsync(TenantModules.BankingMicrofinance, ModuleTestDb.BankingPrice);

        await using var db = _store.Create();
        var queue = await new ListModuleSubscriptionsHandler(db)
            .Handle(new ListModuleSubscriptionsQuery(ModuleSubscriptionStatus.Requested, null), default);

        queue.Should().HaveCount(2);
        queue.Should().BeInAscendingOrder(i => i.Subscription.CreatedAt);
        queue.Should().OnlyContain(i => i.TenantName == "Caisse Avenir");
        queue.Select(i => i.ModuleLabel).Should().Contain("Banking - Microfinance");
    }
}
