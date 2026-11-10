namespace Sankore.Modules.Administration.Tests.Features;

using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.ComplianceSettings.SwitchKyc;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Administration.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The institution's KYC switch (docs/kyc-par-institution.md, lot 1): off only in socle, read by
/// the team in « Mode test », put back on by a regulated module, and read as ON whenever in doubt.
/// </summary>
public sealed class TenantKycSwitchTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _actor = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _factory;
    private readonly ITenantStore _store = Substitute.For<ITenantStore>();
    private readonly IEventPublisher _publisher = Substitute.For<IEventPublisher>();

    public TenantKycSwitchTests()
    {
        _factory = new TestAdminDbContextFactory(_tenant);
        WithModules(_tenant, []);
    }

    public void Dispose() => _factory.Dispose();

    private void WithModules(Guid tenantId, IReadOnlyList<string>? modules, Guid? parent = null)
        => _store.GetAsync(tenantId, Arg.Any<CancellationToken>())
            .Returns(new TenantInfo(tenantId, "Caisse", "caisse.sankore.local", true, false, null, null,
                ActiveModules: modules, ParentTenantId: parent));

    private TenantKycReader Reader(AdministrationDbContext db)
        => new(db, _store, NullLogger<TenantKycReader>.Instance);

    private async Task<Result<SwitchKycResult>> SwitchAsync(bool enabled, string? restoredBy = null)
    {
        await using var db = _factory.CreateContext();
        var user = Substitute.For<ICurrentUser>();
        user.Id.Returns(_actor);
        user.TeamTenantId.Returns(_tenant);
        var reader = Reader(db);
        return await new SwitchKycHandler(db, user, reader, reader, TimeProvider.System, _publisher)
            .Handle(new SwitchKycCommand(enabled, restoredBy), CancellationToken.None);
    }

    private async Task<TenantKycState> StateAsync(Guid? tenantId = null)
    {
        await using var db = _factory.CreateContext();
        return await Reader(db).GetAsync(tenantId ?? _tenant, CancellationToken.None);
    }

    [Fact]
    public async Task An_institution_that_never_touched_it_runs_KYC()
    {
        var state = await StateAsync();

        state.SwitchedOn.Should().BeTrue();
        state.InForce.Should().BeTrue();
        ComplianceSettings.CreateDefault(_tenant, _actor, DateTimeOffset.UtcNow).KycEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task In_socle_only_the_administrator_switches_it_off_and_it_is_announced()
    {
        var result = await SwitchAsync(false);

        result.IsSuccess.Should().BeTrue();
        result.Value.Changed.Should().BeTrue();
        (await StateAsync()).InForce.Should().BeFalse();
        await _publisher.Received(1).PublishAsync(
            Arg.Is<TenantKycSwitchedEvent>(e => e.TenantId == _tenant && !e.Enabled && e.ActorId == _actor),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(TenantModules.BankingMicrofinance)]
    [InlineData(TenantModules.Insurance)]
    public async Task A_regulated_module_keeps_KYC_and_refuses_the_switch(string module)
    {
        WithModules(_tenant, [module]);

        var result = await SwitchAsync(false);

        result.Error.Should().Be(TenantKycErrors.RequiredByModule);
        await using var db = _factory.CreateContext();
        (await db.ComplianceSettings.AnyAsync()).Should().BeFalse();
        await _publisher.DidNotReceiveWithAnyArgs().PublishAsync(default(TenantKycSwitchedEvent)!, default);
    }

    [Fact]
    public async Task Saying_what_it_already_says_announces_nothing()
    {
        await SwitchAsync(false);
        _publisher.ClearReceivedCalls();

        var again = await SwitchAsync(false);
        using var untouched = new TenantKycSwitchTests();
        var onWithoutRow = await untouched.SwitchAsync(true);

        again.Value.Changed.Should().BeFalse();
        onWithoutRow.Value.Changed.Should().BeFalse();
        await _publisher.DidNotReceiveWithAnyArgs().PublishAsync(default(TenantKycSwitchedEvent)!, default);
    }

    [Fact]
    public async Task A_module_activated_later_keeps_KYC_in_force_even_while_the_switch_still_says_off()
    {
        await SwitchAsync(false);
        WithModules(_tenant, [TenantModules.Insurance]);

        var state = await StateAsync();

        state.SwitchedOn.Should().BeFalse();
        state.RequiredByModule.Should().Be(TenantModules.Insurance);
        state.InForce.Should().BeTrue();
    }

    [Fact]
    public async Task The_restorer_puts_the_switch_back_on_once_a_module_is_active()
    {
        await SwitchAsync(false);
        WithModules(_tenant, [TenantModules.BankingMicrofinance]);
        _publisher.ClearReceivedCalls();

        await using var db = _factory.CreateContext();
        var sender = Substitute.For<ISender>();
        sender.Send(Arg.Any<SwitchKycCommand>(), Arg.Any<CancellationToken>())
            .Returns(ci => SwitchAsync(ci.Arg<SwitchKycCommand>().Enabled, ci.Arg<SwitchKycCommand>().RestoredByModule));

        var restored = await new KycModuleRestorer(_store, Reader(db), sender, NullLogger<KycModuleRestorer>.Instance)
            .RestoreAsync(_tenant, CancellationToken.None);

        restored.Should().BeTrue();
        (await StateAsync()).SwitchedOn.Should().BeTrue();
        await _publisher.Received(1).PublishAsync(
            Arg.Is<TenantKycSwitchedEvent>(e => e.Enabled && e.RestoredByModule == TenantModules.BankingMicrofinance),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_restorer_leaves_a_socle_institution_alone()
    {
        await SwitchAsync(false);

        await using var db = _factory.CreateContext();
        var sender = Substitute.For<ISender>();
        var restored = await new KycModuleRestorer(_store, Reader(db), sender, NullLogger<KycModuleRestorer>.Instance)
            .RestoreAsync(_tenant, CancellationToken.None);

        restored.Should().BeFalse();
        await sender.DidNotReceiveWithAnyArgs().Send(default(SwitchKycCommand)!, default);
    }

    [Fact]
    public async Task A_test_twin_reads_its_production_switch()
    {
        await SwitchAsync(false);
        var twin = Guid.NewGuid();
        WithModules(twin, [], parent: _tenant);

        (await StateAsync(twin)).InForce.Should().BeFalse();
    }

    [Fact]
    public async Task An_unreadable_answer_is_in_force()
    {
        await SwitchAsync(false);
        _store.GetAsync(_tenant, Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("registry down"));

        (await StateAsync()).InForce.Should().BeTrue();
    }

    [Fact]
    public void An_unknown_module_list_reads_as_regulated()
        => TenantKycExtensions.RegulatedModuleIn(null).Should().Be(TenantModules.BankingMicrofinance);
}
