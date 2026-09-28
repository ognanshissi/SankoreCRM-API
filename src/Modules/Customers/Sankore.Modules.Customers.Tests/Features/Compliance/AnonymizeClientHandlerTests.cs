namespace Sankore.Modules.Customers.Tests.Features.Compliance;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Compliance.Retention.AnonymizeClient;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;
using Xunit;

/// <summary>
/// US-M01-BE-29, execution side. Three gates must all open — retention elapsed, KYC released,
/// caller inside the perimeter — because the operation cannot be undone.
/// </summary>
public sealed class AnonymizeClientHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _agencyId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly TestCustomersDbContextFactory _factory;
    private readonly IFieldEncryptor _encryptor = TestDoubles.Encryptor();
    private readonly IBlindIndexer _indexer = TestDoubles.Indexer();
    private readonly IEventPublisher _publisher = Substitute.For<IEventPublisher>();
    private readonly FixedTimeProvider _clock = new(DateTimeOffset.UtcNow.AddYears(11));

    private const string Reason = "GDPR erasure request REF-2026-0041.";

    public AnonymizeClientHandlerTests() => _factory = new TestCustomersDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task An_eligible_archived_client_is_anonymized_and_the_event_is_published()
    {
        var client = await SeedArchivedAsync("CLI-2001");

        await using var db = _factory.CreateContext();

        var result = await NewHandler(db).Handle(
            new AnonymizeClientCommand(client.Id, Reason), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await using var reread = _factory.CreateContext();
        var stored = await reread.Clients
            .Include(c => c.ContactPoints)
            .SingleAsync(c => c.Id == client.Id);

        stored.IsAnonymized.Should().BeTrue();
        stored.FirstName.Should().BeNull();
        stored.LastName.Should().BeNull();
        stored.EncryptedIdentityDocumentNumber.Should().BeNull();
        stored.IdentityDocumentNumberBlindIndex.Should().BeNull();
        stored.PhoneticKeyPrimary.Should().BeNull();
        stored.ContactPoints.Should().OnlyContain(cp => !cp.IsActive);

        // The client number survives: accounting history keeps a stable reference.
        stored.ClientNumber.Should().Be("CLI-2001");

        await _publisher.Received(1).PublishAsync(
            Arg.Is<ClientAnonymizedEvent>(e => e.TenantId == _tenantId && e.ClientId == client.Id),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_client_still_inside_the_retention_window_is_refused()
    {
        var client = await SeedArchivedAsync("CLI-2002");

        // Only days since the archive.
        _clock.Now = DateTimeOffset.UtcNow.AddDays(2);

        await using var db = _factory.CreateContext();

        var result = await NewHandler(db).Handle(
            new AnonymizeClientCommand(client.Id, Reason), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.RetentionNotReached);
        await AssertNothingPublishedAsync();
    }

    [Fact]
    public async Task A_client_that_is_not_archived_is_refused()
    {
        Client client;
        await using (var seed = _factory.CreateContext())
        {
            client = ComplianceTestData.Individual(
                _tenantId, _agencyId, "CLI-2003", "Yao", "Traore", _userId, _encryptor, _indexer,
                documentNumber: "CI0123456789");

            seed.Clients.Add(client);
            await seed.SaveChangesAsync();
        }

        await using var db = _factory.CreateContext();

        var result = await NewHandler(db).Handle(
            new AnonymizeClientCommand(client.Id, Reason), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.RetentionNotReached);
    }

    [Fact]
    public async Task A_client_whose_KYC_file_is_not_released_is_refused()
    {
        var client = await SeedArchivedAsync("CLI-2004");

        await using var db = _factory.CreateContext();

        var result = await NewHandler(db, retentionCleared: false).Handle(
            new AnonymizeClientCommand(client.Id, Reason), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.KycRetentionNotCleared);

        await using var reread = _factory.CreateContext();
        (await reread.Clients.SingleAsync(c => c.Id == client.Id)).IsAnonymized.Should().BeFalse();
        await AssertNothingPublishedAsync();
    }

    [Fact]
    public async Task An_unknown_client_answers_CLIENT_NOT_FOUND()
    {
        await using var db = _factory.CreateContext();

        var result = await NewHandler(db).Handle(
            new AnonymizeClientCommand(Guid.NewGuid(), Reason), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientNotFound);
    }

    [Fact]
    public async Task A_missing_reason_answers_REASON_REQUIRED()
    {
        var client = await SeedArchivedAsync("CLI-2005");

        await using var db = _factory.CreateContext();

        var result = await NewHandler(db).Handle(
            new AnonymizeClientCommand(client.Id, "   "), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ReasonRequired);
    }

    [Fact]
    public async Task A_client_outside_the_callers_agency_perimeter_answers_CLIENT_NOT_FOUND()
    {
        var client = await SeedArchivedAsync("CLI-2006");

        await using var db = _factory.CreateContext();

        // 404 and not 403: a 403 would confirm that a client with this id exists.
        var result = await NewHandler(db, agencyScope: TestDoubles.AgencyScope(Guid.NewGuid()))
            .Handle(new AnonymizeClientCommand(client.Id, Reason), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientNotFound);
        await AssertNothingPublishedAsync();
    }

    [Fact]
    public async Task The_SYSTEM_account_is_not_blocked_by_the_agency_perimeter()
    {
        // The monthly job runs as Guid.Empty, which belongs to no agency: asking the scope
        // provider about it returns an empty set, so an unguarded perimeter check would make the
        // job unable to anonymize anything at all.
        var client = await SeedArchivedAsync("CLI-2007");

        await using var db = _factory.CreateContext();

        var systemScope = Substitute.For<IAgencyScopeProvider>();
        systemScope.CanAccessAgencyAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        var handler = new AnonymizeClientHandler(
            db,
            TestDoubles.CurrentUser(_tenantId, Guid.Empty, "System"),
            systemScope,
            RetentionSettings(),
            Kyc(retentionCleared: true),
            _publisher,
            _clock,
            NullLogger<AnonymizeClientHandler>.Instance);

        var result = await handler.Handle(
            new AnonymizeClientCommand(client.Id, "Monthly retention job."), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await systemScope.DidNotReceiveWithAnyArgs()
            .CanAccessAgencyAsync(default, default, default, default);
    }

    [Fact]
    public async Task Anonymizing_twice_is_a_no_op_and_republishes_nothing()
    {
        var client = await SeedArchivedAsync("CLI-2008");

        await using (var first = _factory.CreateContext())
        {
            (await NewHandler(first).Handle(
                new AnonymizeClientCommand(client.Id, Reason), CancellationToken.None))
                .IsSuccess.Should().BeTrue();
        }

        _publisher.ClearReceivedCalls();

        await using var second = _factory.CreateContext();
        var result = await NewHandler(second).Handle(
            new AnonymizeClientCommand(client.Id, Reason), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await AssertNothingPublishedAsync();
    }

    [Fact]
    public async Task A_client_of_another_tenant_is_invisible()
    {
        var otherTenantId = Guid.NewGuid();

        Client foreignClient;
        await using (var seed = _factory.CreateContext())
        {
            foreignClient = ComplianceTestData.ArchivedIndividual(
                otherTenantId, _agencyId, "CLI-8888", _userId, _encryptor, _indexer);

            seed.Clients.Add(foreignClient);
            await seed.SaveChangesAsync();
        }

        await using var db = _factory.CreateContext();

        var result = await NewHandler(db).Handle(
            new AnonymizeClientCommand(foreignClient.Id, Reason), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientNotFound);

        await using var reread = _factory.CreateContext();
        var stillThere = await reread.Clients
            .IgnoreQueryFilters()
            .SingleAsync(c => c.Id == foreignClient.Id);

        stillThere.IsAnonymized.Should().BeFalse();
    }

    [Fact]
    public void The_command_is_audited_and_names_the_client_it_erases()
    {
        // The audit entry is the only trace left once the personal data is gone.
        var command = new AnonymizeClientCommand(Guid.NewGuid(), Reason);

        command.Should().BeAssignableTo<Sankore.Shared.Infrastructure.Behaviors.ICommand>();
        command.Should().BeAssignableTo<IResourceCommand>();
        command.ResourceType.Should().Be("Client");
        command.ResourceId.Should().Be(command.ClientId.ToString());
    }

    [Fact]
    public void The_validator_demands_a_substantial_reason()
    {
        var validator = new AnonymizeClientValidator();

        validator.Validate(new AnonymizeClientCommand(Guid.NewGuid(), "")).IsValid.Should().BeFalse();
        validator.Validate(new AnonymizeClientCommand(Guid.NewGuid(), "ok")).IsValid.Should().BeFalse();
        validator.Validate(new AnonymizeClientCommand(Guid.Empty, Reason)).IsValid.Should().BeFalse();
        validator.Validate(new AnonymizeClientCommand(Guid.NewGuid(), Reason)).IsValid.Should().BeTrue();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private AnonymizeClientHandler NewHandler(
        CustomersDbContext db,
        bool retentionCleared = true,
        IAgencyScopeProvider? agencyScope = null) =>
        new(db,
            TestDoubles.CurrentUser(_tenantId, _userId, "Administrator"),
            agencyScope ?? TestDoubles.AgencyScope(),
            RetentionSettings(),
            Kyc(retentionCleared),
            _publisher,
            _clock,
            NullLogger<AnonymizeClientHandler>.Instance);

    private ICustomerSettings RetentionSettings() =>
        TestDoubles.Settings(_tenantId, (CustomerSettingKeys.RetentionYears, "10"));

    private static IKycModule Kyc(bool retentionCleared)
    {
        var kyc = Substitute.For<IKycModule>();
        kyc.IsRetentionClearedAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(retentionCleared));

        return kyc;
    }

    private async Task<Client> SeedArchivedAsync(string clientNumber)
    {
        await using var db = _factory.CreateContext();

        var client = ComplianceTestData.ArchivedIndividual(
            _tenantId, _agencyId, clientNumber, _userId, _encryptor, _indexer);

        db.Clients.Add(client);
        await db.SaveChangesAsync();

        return client;
    }

    private async Task AssertNothingPublishedAsync() =>
        await _publisher.DidNotReceiveWithAnyArgs()
            .PublishAsync<ClientAnonymizedEvent>(default!, default);
}
