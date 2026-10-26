namespace Sankore.Modules.Integration.Tests.Features.Insurance;

using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Integration.Adapters.Fake;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Commands;
using Sankore.Modules.Integration.Features.Insurance;
using Sankore.Modules.Integration.Features.Insurance.HoldingSubscription;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Infrastructure.Crypto;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.Features.Commands;
using Sankore.Modules.Integration.Tests.Features.Onboarding;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// BNF-B-16: a signed <c>ProductHolding</c> queued for its back office becomes exactly one
/// <c>IntegrationCommand</c> — keyed by the holding and its clause version, so an avenant is a
/// second send and a redelivery is not.
/// </summary>
public sealed class HoldingSubscriptionConsumerTests : IDisposable
{
    private readonly Guid _tenant = new("aaaaaaaa-1111-0000-0000-000000000001");
    private readonly Guid _clientId = new("bbbbbbbb-1111-0000-0000-000000000002");
    private readonly Guid _holdingId = new("dddddddd-1111-0000-0000-000000000004");
    private readonly Guid _lineId = new("eeeeeeee-1111-0000-0000-000000000005");
    private readonly Guid _connectionId = new("cccccccc-1111-0000-0000-000000000003");
    private readonly Guid _agent = new("ffffffff-1111-0000-0000-000000000006");

    private readonly string _databaseName = $"holding-subscription-{Guid.NewGuid()}";
    private readonly ICustomersModule _customers = Substitute.For<ICustomersModule>();
    private readonly Sankore.Modules.Leads.PublicApi.ILeadsModule _leads =
        Substitute.For<Sankore.Modules.Leads.PublicApi.ILeadsModule>();
    private readonly OnboardingTestContext.RecordingInboxGuard _inbox = new();
    private readonly CommandsTestHarness.FixedClock _clock = new(CommandsTestHarness.Now);
    private readonly ServiceProvider _provider;

    /// <summary>The tenant's paid modules; every module unless a test switches them off.</summary>
    private ITenantModules _modules = FixedTenantModules.All;

    public HoldingSubscriptionConsumerTests()
    {
        _provider = BuildProvider();
        SeedInsuranceConnection();
        HoldingIs(Holding());
    }

    public void Dispose() => _provider.Dispose();

    // ── fixtures ────────────────────────────────────────────────────────────

    private HoldingForSync Holding(
        bool signed = true,
        string? externalSystem = "Fake",
        int clauseVersion = 1,
        IReadOnlyList<HoldingDesignationForSync>? designations = null)
        => new(
            HoldingId:          _holdingId,
            ClientId:           _clientId,
            ProductLineId:      _lineId,
            ProductCode:        "VIE-01",
            ProductCategory:    nameof(ProductCategory.Insurance),
            Status:             "PendingSync",
            ClauseVersion:      clauseVersion,
            IsSigned:           signed,
            SignedAt:           signed ? CommandsTestHarness.Now : null,
            SignatureReference: signed ? "doc://signature/1" : null,
            ExternalSystem:     externalSystem,
            InsuredCapital:     5_000_000m,
            Premium:            25_000m,
            Periodicity:        "Monthly",
            Currency:           "XOF",
            EffectiveFrom:      new DateOnly(2026, 11, 1),
            Designations:       designations ??
            [
                new HoldingDesignationForSync(
                    "BENEFICIAIREDECES", "Awa Ouattara", "Épouse", 1, 100m, new DateOnly(1990, 4, 2)),
            ]);

    private void HoldingIs(HoldingForSync? holding)
        => _customers.GetHoldingForSyncAsync(_tenant, _holdingId, Arg.Any<CancellationToken>())
                     .Returns(holding);

    private ProductSubscriptionRequestedEvent Event(int clauseVersion = 1) => new(
        TenantId:      _tenant,
        ClientId:      _clientId,
        HoldingId:     _holdingId,
        ProductLineId: _lineId,
        ProductCode:   "VIE-01",
        ClauseVersion: clauseVersion,
        RequestedBy:   _agent);

    private Task ConsumeAsync(ProductSubscriptionRequestedEvent? evt = null, Guid? messageId = null)
    {
        var consumer = new HoldingSubscriptionConsumer(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<HoldingSubscriptionConsumer>.Instance);

        var message = evt ?? Event();
        var context = Substitute.For<ConsumeContext<ProductSubscriptionRequestedEvent>>();
        context.Message.Returns(message);
        context.MessageId.Returns(messageId ?? Guid.NewGuid());
        context.CancellationToken.Returns(CancellationToken.None);

        return consumer.Consume(context);
    }

    private IntegrationDbContext NewDb()
        => new(
            new DbContextOptionsBuilder<IntegrationDbContext>()
                .UseInMemoryDatabase(_databaseName)
                .Options,
            new FixedTenantContext(_tenant));

    private async Task<List<IntegrationCommand>> CommandsAsync()
    {
        await using var db = NewDb();
        return await db.Commands.IgnoreQueryFilters()
            .Where(c => c.TenantId == _tenant)
            .ToListAsync(CancellationToken.None);
    }

    // ── the point of the feature ────────────────────────────────────────────

    [Fact]
    public async Task Without_the_Assurance_module_a_new_subscription_is_not_sent_to_the_insurer()
    {
        _modules = new FixedTenantModules(TenantModules.BankingMicrofinance);

        await ConsumeAsync();

        (await CommandsAsync()).Should().BeEmpty("the holding stays PendingSync, nothing leaves for the insurer");
    }

    [Fact]
    public async Task A_signed_holding_becomes_one_queued_command()
    {
        await ConsumeAsync();

        var command = (await CommandsAsync()).Should().ContainSingle().Subject;

        command.CommandType.Should().Be(CommandType.SubscribePolicy);
        command.EntityType.Should().Be(IntegrationEntityTypes.Policy);
        command.CrmId.Should().Be(_holdingId,
            "the return path has to say WHICH holding the insurer answered about");
        command.Status.Should().Be(CommandStatus.Pending);
        command.ConnectionId.Should().Be(_connectionId);
    }

    [Fact]
    public async Task The_payload_is_encrypted_and_only_its_field_names_are_in_clear()
    {
        await ConsumeAsync();

        var command = (await CommandsAsync()).Single();

        command.PayloadEncrypted.Should().NotBeNull();
        command.PayloadEncrypted.Should().NotContain("Ouattara",
            "a beneficiary clause names who receives a capital");
        // camelCase, as the protector serialises them. Asserted in that form rather than
        // normalised, because the stored string IS what somebody reads off a rejection queue.
        command.PayloadFieldNames.Should().NotBeNull();
        command.PayloadFieldNames.Should().Contain("beneficiaries");
        command.PayloadFieldNames.Should().Contain("crmHoldingId");
        command.PayloadFieldNames.Should().Contain("clauseVersion");
    }

    // ── idempotence (BNF-B-16) ──────────────────────────────────────────────

    [Fact]
    public async Task A_redelivery_queues_nothing_more()
    {
        var messageId = Guid.NewGuid();

        await ConsumeAsync(messageId: messageId);
        await ConsumeAsync(messageId: messageId);

        (await CommandsAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task A_different_message_for_the_same_clause_is_still_one_command()
    {
        // The inbox cannot catch this — two message ids — so the idempotency KEY must.
        await ConsumeAsync();
        await ConsumeAsync();

        (await CommandsAsync()).Should().ContainSingle(
            "the key is derived from the holding and the clause version, not from the message");
    }

    [Fact]
    public async Task An_avenant_is_a_SECOND_send()
    {
        // The clause changed, so the insurer has to be told. Without the version in the key this
        // would be swallowed as a replay and the insurer would never learn about it.
        await ConsumeAsync();

        HoldingIs(Holding(clauseVersion: 2));
        await ConsumeAsync(Event(clauseVersion: 2));

        var commands = await CommandsAsync();
        commands.Should().HaveCount(2);
        commands.Select(c => c.IdempotencyKey).Distinct().Should().HaveCount(2);
        commands.Should().OnlyContain(c => c.CrmId == _holdingId);
    }

    // ── what must never be sent ─────────────────────────────────────────────

    [Fact]
    public async Task An_UNSIGNED_holding_is_never_queued()
    {
        // The last code that runs before a contract leaves the institution. The transition to
        // PendingSync already required a signature; this consumer must not take a second-hand
        // word for the client's consent.
        HoldingIs(Holding(signed: false));

        await ConsumeAsync();

        (await CommandsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_CRM_only_product_line_is_not_pushed_anywhere()
    {
        HoldingIs(Holding(externalSystem: null));

        await ConsumeAsync();

        (await CommandsAsync()).Should().BeEmpty(
            "null is also what an unreachable catalogue yields, and both mean do not send");
    }

    [Fact]
    public async Task A_holding_that_no_longer_exists_is_not_an_incident()
    {
        HoldingIs(null);

        var act = async () => await ConsumeAsync();

        await act.Should().NotThrowAsync();
        (await CommandsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task The_SYSTEM_placeholder_tenant_is_skipped()
    {
        await ConsumeAsync(Event() with { TenantId = Guid.Empty });

        (await CommandsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_tenant_with_no_insurance_connection_is_logged_and_not_retried_for_ever()
    {
        // An administrator's to clear. Rethrowing would have the broker redeliver against a
        // condition only a human can change; the holding stays in PendingSync, which is where a
        // reconciliation run looks for it.
        using var fresh = new HoldingSubscriptionConsumerTests();
        fresh.ClearConnections();

        var act = async () => await fresh.ConsumeAsync();

        await act.Should().NotThrowAsync();
        (await fresh.CommandsAsync()).Should().BeEmpty();
    }

    // ── the mapping (BNF-B-16) ──────────────────────────────────────────────

    [Fact]
    public void The_payload_carries_the_snapshotted_code_and_the_clause()
    {
        var payload = HoldingPolicyPayload.From(Holding());

        payload.CrmHoldingId.Should().Be(_holdingId);
        payload.ClauseVersion.Should().Be(1);
        payload.InsurerProductCode.Should().Be("VIE-01",
            "the code as snapshotted at subscription, not the catalogue's code today");
        payload.EffectiveDate.Should().Be(new DateOnly(2026, 11, 1));
        payload.Periodicity.Should().Be(PremiumPeriodicity.Monthly);
        payload.ConsentEvidenceRef.Should().Be("doc://signature/1",
            "the signature IS the consent evidence for a subscription");

        var beneficiary = payload.Beneficiaries.Should().ContainSingle().Subject;
        beneficiary.FullName.Should().Be("Awa Ouattara");
        beneficiary.SharePercent.Should().Be(100m);
        beneficiary.DateOfBirth.Should().Be(new DateOnly(1990, 4, 2));
    }

    [Fact]
    public void A_role_with_no_share_renders_as_zero_and_never_as_a_hundred()
    {
        // A proxy or a co-holder carries no quote-part, and asserting an allocation nobody made
        // would have the insurer pay the wrong person.
        var payload = HoldingPolicyPayload.From(Holding(designations:
        [
            new HoldingDesignationForSync("MANDATAIRE", "Kofi Ouattara", null, 1, null, null),
        ]));

        var beneficiary = payload.Beneficiaries.Should().ContainSingle().Subject;
        beneficiary.SharePercent.Should().Be(0m);
        beneficiary.Relationship.Should().Be("MANDATAIRE",
            "the role is more than nothing when the agent typed no relationship");
    }

    [Fact]
    public void An_unrecognised_periodicity_falls_back_to_a_single_premium()
    {
        HoldingPolicyPayload.From(Holding() with { Periodicity = "Fortnightly" })
            .Periodicity.Should().Be(PremiumPeriodicity.Single);
    }

    // ── harness ─────────────────────────────────────────────────────────────

    internal void ClearConnections()
    {
        using var db = NewDb();
        db.Connections.RemoveRange(db.Connections.IgnoreQueryFilters().ToList());
        db.SaveChanges();
    }

    // ── an insurance line: the holding leaves through its prepared subscription ─

    private InsuranceSubscription SeedPrepared(
        PremiumPaymentMode mode, Guid? opportunity = null, string? answersSha256 = null)
    {
        using var db = NewDb();

        var product = InsuranceTestHarness.SeedProduct(
            db, _tenant, _connectionId, insurerProductCode: "ORASS-OBS-01", crmProductCode: "VIE-01");

        var saga = InsuranceSubscription.Prepare(
            tenantId: _tenant,
            connectionId: _connectionId,
            crmCustomerId: _clientId,
            productId: product.Id,
            effectiveDate: new DateOnly(2026, 11, 1),
            premiumAmount: 25_000m,
            currency: "XOF",
            createdBy: _agent,
            clock: _clock,
            holdingId: _holdingId,
            clauseVersion: 1,
            paymentMode: mode,
            cbsAccountRef: mode == PremiumPaymentMode.CoreBankingDebit ? "CI-00042" : null,
            paymentMethod: mode == PremiumPaymentMode.RecordedByAgent ? RecordedPaymentMethod.Cash : null,
            paymentReference: mode == PremiumPaymentMode.RecordedByAgent ? "REC-0001" : null,
            paidOn: mode == PremiumPaymentMode.RecordedByAgent ? new DateOnly(2026, 10, 1) : null,
            sourceOpportunityId: opportunity,
            answersSha256: answersSha256);

        db.InsuranceSubscriptions.Add(saga);
        db.SaveChanges();
        return saga;
    }

    private async Task<InsuranceSubscription> SagaAsync(Guid id)
    {
        await using var db = NewDb();
        return await db.InsuranceSubscriptions.IgnoreQueryFilters().SingleAsync(s => s.Id == id);
    }

    [Fact]
    public async Task A_premium_recorded_by_the_agent_sends_the_holding_to_the_insurer_at_once()
    {
        var saga = SeedPrepared(PremiumPaymentMode.RecordedByAgent);

        await ConsumeAsync();

        var commands = await CommandsAsync();
        commands.Should().ContainSingle(c => c.CommandType == CommandType.SubscribePolicy)
            .Which.CrmId.Should().Be(_holdingId, "the insurer's answer is addressed to the holding");

        var reloaded = await SagaAsync(saga.Id);
        reloaded.Status.Should().Be(SubscriptionStatus.PaymentRecorded);
        reloaded.SubscribeCommandId.Should().Be(commands.Single().Id);
    }

    [Fact]
    public async Task When_the_insurer_collects_the_holding_is_sent_at_once_and_the_premium_stays_owed()
    {
        var saga = SeedPrepared(PremiumPaymentMode.InsurerCollects);

        await ConsumeAsync();

        (await CommandsAsync()).Should().ContainSingle(c => c.CommandType == CommandType.SubscribePolicy);
        var reloaded = await SagaAsync(saga.Id);
        reloaded.Status.Should().Be(SubscriptionStatus.AwaitingPremium);
        reloaded.IsPremiumOutstanding.Should().BeTrue("the holding waits « en attente de paiement »");
    }

    [Fact]
    public async Task A_debit_with_no_core_banking_left_abandons_the_subscription_and_rejects_the_holding()
    {
        // Prepared while the institution had a core banking connection; none is active now.
        var saga = SeedPrepared(PremiumPaymentMode.CoreBankingDebit);
        _customers.RejectHoldingSyncAsync(_tenant, _holdingId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok());

        await ConsumeAsync();

        (await CommandsAsync()).Should().BeEmpty("nothing may reach the insurer before the premium");
        (await SagaAsync(saga.Id)).Status.Should().Be(SubscriptionStatus.Cancelled);
        await _customers.Received(1).RejectHoldingSyncAsync(
            _tenant, _holdingId, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_insurance_line_sent_with_no_prepared_subscription_is_rejected_not_sent()
    {
        // The generic « Envoyer » on an insurance holding: no premium, no consent — it used to
        // reach the insurer and become Active with nothing collected.
        await using (var db = NewDb())
            InsuranceTestHarness.SeedProduct(db, _tenant, _connectionId, crmProductCode: "VIE-01");

        _customers.RejectHoldingSyncAsync(_tenant, _holdingId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok());

        await ConsumeAsync();

        (await CommandsAsync()).Should().BeEmpty();
        await _customers.Received(1).RejectHoldingSyncAsync(
            _tenant, _holdingId,
            Arg.Is<string>(r => r.StartsWith(Sankore.Modules.Integration.Features.Insurance.SubscribePolicy.InsuranceErrors.NotPrepared)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Answers_edited_after_the_signature_stop_the_subscription()
    {
        var opportunity = Guid.NewGuid();
        var saga = SeedPrepared(PremiumPaymentMode.RecordedByAgent, opportunity, answersSha256: "signed");

        _leads.GetProjectAnswersSnapshotAsync(_tenant, opportunity, Arg.Any<CancellationToken>())
            .Returns(new Sankore.Modules.Leads.PublicApi.ProjectAnswersSnapshot(true, 0, true, "[]", "edited"));
        _customers.RejectHoldingSyncAsync(_tenant, _holdingId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok());

        await ConsumeAsync();

        (await CommandsAsync()).Should().BeEmpty("the insurer would hold a declaration nobody signed");
        (await SagaAsync(saga.Id)).Status.Should().Be(SubscriptionStatus.Cancelled);
        await _customers.Received(1).RejectHoldingSyncAsync(
            _tenant, _holdingId, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _leads.DidNotReceive().MarkProjectAnswersTransmittedAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unchanged_answers_leave_with_the_holding_and_are_then_frozen()
    {
        var opportunity = Guid.NewGuid();
        SeedPrepared(PremiumPaymentMode.RecordedByAgent, opportunity, answersSha256: "signed");

        _leads.GetProjectAnswersSnapshotAsync(_tenant, opportunity, Arg.Any<CancellationToken>())
            .Returns(new Sankore.Modules.Leads.PublicApi.ProjectAnswersSnapshot(true, 0, true, "[]", "signed"));

        await ConsumeAsync();

        (await CommandsAsync()).Should().ContainSingle(c => c.CommandType == CommandType.SubscribePolicy);
        await _leads.Received(1).MarkProjectAnswersTransmittedAsync(_tenant, opportunity, Arg.Any<CancellationToken>());
    }

    private void SeedInsuranceConnection()
    {
        using var db = NewDb();

        var connection = IntegrationConnection.Create(
            tenantId: _tenant,
            family: IntegrationFamily.Insurance,
            kind: IntegrationKind.Fake,
            mode: IntegrationMode.Api,
            name: "Assureur de test",
            settings: new FakeSettings { Family = IntegrationFamily.Insurance },
            createdBy: CommandsTestHarness.Actor,
            clock: _clock,
            id: _connectionId);

        connection.RecordHealth(
            IntegrationHealth.Healthy(TimeSpan.FromMilliseconds(80), CommandsTestHarness.Now), _clock);

        var activated = connection.Activate(CommandsTestHarness.Actor, _clock);
        if (activated.IsFailure)
            throw new InvalidOperationException($"Fixture could not activate: {activated.Error}");

        db.Connections.Add(connection);
        db.SaveChanges();
    }

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton<TimeProvider>(_clock);

        // Exactly Program.cs's shape, including the absence of an HTTP fallback: these throw when
        // no ambient background scope was established, so a consumer that created its DI scope
        // before calling SetScope fails here instead of queueing into Guid.Empty.
        services.AddScoped<ITenantContext>(_ =>
            BackgroundJobContext.CurrentTenant
            ?? throw new InvalidOperationException("No ambient tenant."));

        services.AddScoped<ICurrentUser>(_ =>
            BackgroundJobContext.CurrentUser
            ?? throw new InvalidOperationException("No ambient user."));

        services.AddDbContext<IntegrationDbContext>(
            opt => opt.UseInMemoryDatabase(_databaseName), ServiceLifetime.Scoped);

        services.AddIntegrationFieldProtection(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{IntegrationFieldProtection.SectionName}:FieldEncryptionKey"] = CommandsTestHarness.TestKey,
                [$"{IntegrationFieldProtection.SectionName}:BlindIndexKey"] = CommandsTestHarness.TestKey,
            })
            .Build());

        services.AddSingleton<IIntegrationInboxGuard>(_inbox);
        services.AddTransient(_ => _modules);
        services.AddScoped<IntegrationAdapterResolver>();
        services.AddScoped<Sankore.Modules.Integration.Features.References.GetReference.ReferenceLookup>();
        services.AddScoped<CommandPayloadProtector>();
        services.AddScoped<CbsCustomerPayloadSource>();
        services.AddSingleton(_customers);
        services.AddSingleton(Substitute.For<Sankore.Modules.Kyc.PublicApi.IKycModule>());
        services.AddScoped<IIntegrationModule, IntegrationModuleFacade>();
        services.AddKeyedSingleton<IInsurancePolicyPort>(IntegrationKind.Fake.ToString(), new FakeAdapter());
        services.AddSingleton(CommandsTestHarness.Encryptor());
        services.AddSingleton(_leads);
        services.AddScoped<HoldingSubscriptionChain>();

        return services.BuildServiceProvider();
    }
}
