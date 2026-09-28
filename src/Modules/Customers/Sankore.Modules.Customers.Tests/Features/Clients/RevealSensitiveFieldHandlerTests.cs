namespace Sankore.Modules.Customers.Tests.Features.Clients;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Clients.RevealSensitiveField;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Outbox;
using Sankore.Shared.Kernel;
using Xunit;

public sealed class RevealSensitiveFieldHandlerTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AgencyId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OtherAgencyId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid ActorId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private const string DocumentNumber = "CI0012345642";
    private const string PhoneNumber = "+22507080918";
    private const string ComplianceEmailKey = "compliance-alert-email";

    private readonly TestCustomersDbContextFactory _factory = new(TenantId);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Returns_the_clear_value_and_records_the_access()
    {
        await using var seed = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(
            seed, TenantId, AgencyId, identityDocumentNumber: DocumentNumber);

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db, out _, out _);

        var result = await handler.Handle(
            new RevealSensitiveFieldCommand(client.Id, SensitiveField.IdentityDocumentNumber, null),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Field.Should().Be(nameof(SensitiveField.IdentityDocumentNumber));
        result.Value.Value.Should().Be(DocumentNumber);

        await using var read = _factory.CreateContext();
        var log = await read.SensitiveDataAccessLogs.SingleAsync();

        log.ActorUserId.Should().Be(ActorId);
        log.ClientId.Should().Be(client.Id);
        log.FieldName.Should().Be(nameof(SensitiveField.IdentityDocumentNumber));
    }

    [Fact]
    public async Task Decrypts_the_requested_field_and_nothing_else()
    {
        await using var seed = _factory.CreateContext();
        var client = TestClientFactory.Individual(
            TenantId, AgencyId, identityDocumentNumber: DocumentNumber);
        client.AddContactPoint(
            ContactPointType.Phone,
            TestDoubles.Encryptor().Encrypt(PhoneNumber)!,
            TestDoubles.Indexer().Compute(BlindIndexPurpose.Phone, PhoneNumber),
            label: null, isPrimary: true, validFrom: DateTimeOffset.UtcNow, actor: ActorId);
        await TestClientFactory.SeedAsync(seed, client);

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db, out var encryptor, out _);

        await handler.Handle(
            new RevealSensitiveFieldCommand(client.Id, SensitiveField.Phone, null),
            CancellationToken.None);

        // Exactly one unwrap: the document number, the date of birth and the income stay
        // encrypted in memory, so a phone reveal cannot incidentally expose them.
        encryptor.DecryptCalls.Should().Be(1);
    }

    [Fact]
    public async Task Reveals_the_named_contact_point_when_the_client_has_several()
    {
        const string secondPhone = "+22501020304";

        await using var seed = _factory.CreateContext();
        var client = TestClientFactory.Individual(TenantId, AgencyId);
        client.AddContactPoint(
            ContactPointType.Phone,
            TestDoubles.Encryptor().Encrypt(PhoneNumber)!,
            TestDoubles.Indexer().Compute(BlindIndexPurpose.Phone, PhoneNumber),
            label: "Principal", isPrimary: true, validFrom: DateTimeOffset.UtcNow, actor: ActorId);

        var other = client.AddContactPoint(
            ContactPointType.Phone,
            TestDoubles.Encryptor().Encrypt(secondPhone)!,
            TestDoubles.Indexer().Compute(BlindIndexPurpose.Phone, secondPhone),
            label: "Boutique", isPrimary: false, validFrom: DateTimeOffset.UtcNow, actor: ActorId);

        await TestClientFactory.SeedAsync(seed, client);

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db, out _, out _);

        var result = await handler.Handle(
            new RevealSensitiveFieldCommand(client.Id, SensitiveField.Phone, other.Id),
            CancellationToken.None);

        result.Value.Value.Should().Be(secondPhone);
    }

    [Fact]
    public async Task Falls_back_to_the_primary_contact_point_when_no_id_is_given()
    {
        await using var seed = _factory.CreateContext();
        var client = TestClientFactory.Individual(TenantId, AgencyId);
        client.AddContactPoint(
            ContactPointType.Phone,
            TestDoubles.Encryptor().Encrypt("+22509090909")!,
            TestDoubles.Indexer().Compute(BlindIndexPurpose.Phone, "+22509090909"),
            label: null, isPrimary: false, validFrom: DateTimeOffset.UtcNow, actor: ActorId);
        client.AddContactPoint(
            ContactPointType.Phone,
            TestDoubles.Encryptor().Encrypt(PhoneNumber)!,
            TestDoubles.Indexer().Compute(BlindIndexPurpose.Phone, PhoneNumber),
            label: null, isPrimary: true, validFrom: DateTimeOffset.UtcNow, actor: ActorId);
        await TestClientFactory.SeedAsync(seed, client);

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db, out _, out _);

        var result = await handler.Handle(
            new RevealSensitiveFieldCommand(client.Id, SensitiveField.Phone, null),
            CancellationToken.None);

        result.Value.Value.Should().Be(PhoneNumber);
    }

    [Fact]
    public async Task Fails_with_unknown_sensitive_field_when_the_client_stores_nothing_for_it()
    {
        await using var seed = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(seed, TenantId, AgencyId);

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db, out _, out _);

        // An individual client has no RCCM.
        var result = await handler.Handle(
            new RevealSensitiveFieldCommand(client.Id, SensitiveField.RegistrationNumber, null),
            CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.UnknownSensitiveField);

        await using var read = _factory.CreateContext();
        // Nothing was revealed, so nothing is logged.
        (await read.SensitiveDataAccessLogs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Refuses_past_the_hourly_quota_and_publishes_the_security_event()
    {
        await using var seed = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(
            seed, TenantId, AgencyId, identityDocumentNumber: DocumentNumber);

        await SeedAccessLogsAsync(seed, client.Id, count: 3, ageMinutes: 10);

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db, out _, out var notifications, revealLimitPerHour: 3);

        var result = await handler.Handle(
            new RevealSensitiveFieldCommand(client.Id, SensitiveField.IdentityDocumentNumber, null),
            CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.RevealRateLimitExceeded);

        await using var read = _factory.CreateContext();
        var outbox = await read.Set<OutboxMessage>().SingleAsync();
        outbox.EventType.Should().Contain(nameof(SensitiveRevealThresholdExceededEvent));

        // No compliance mailbox configured for this tenant: the alert is logged, not mailed.
        await notifications.DidNotReceive()
            .QueueEmailAsync(Arg.Any<QueueEmailRequest>(), Arg.Any<CancellationToken>());

        // The refused attempt is not counted as an access — nothing was revealed.
        (await read.SensitiveDataAccessLogs.CountAsync()).Should().Be(3);
    }

    [Fact]
    public async Task Mails_the_compliance_officer_when_the_tenant_configured_one()
    {
        await using var seed = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(
            seed, TenantId, AgencyId, identityDocumentNumber: DocumentNumber);

        await SeedAccessLogsAsync(seed, client.Id, count: 2, ageMinutes: 5);

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(
            db, out _, out var notifications,
            revealLimitPerHour: 2,
            settingOverrides: [(ComplianceEmailKey, "conformite@sankore.test")]);

        var result = await handler.Handle(
            new RevealSensitiveFieldCommand(client.Id, SensitiveField.IdentityDocumentNumber, null),
            CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.RevealRateLimitExceeded);

        await notifications.Received(1).QueueEmailAsync(
            Arg.Is<QueueEmailRequest>(r =>
                r.TemplateKey == "customers.reveal-threshold-exceeded"
                && r.RecipientEmail == "conformite@sankore.test"
                && r.TenantId == TenantId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Ignores_accesses_older_than_the_rolling_hour()
    {
        await using var seed = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(
            seed, TenantId, AgencyId, identityDocumentNumber: DocumentNumber);

        // Yesterday's reveals must not hold today's operator hostage.
        await SeedAccessLogsAsync(seed, client.Id, count: 10, ageMinutes: 24 * 60);

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db, out _, out _, revealLimitPerHour: 3);

        var result = await handler.Handle(
            new RevealSensitiveFieldCommand(client.Id, SensitiveField.IdentityDocumentNumber, null),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Counts_the_quota_per_user_across_every_client()
    {
        await using var seed = _factory.CreateContext();
        var target = await TestClientFactory.SeedIndividualAsync(
            seed, TenantId, AgencyId, clientNumber: "ABJ-2026-000200",
            identityDocumentNumber: DocumentNumber);

        var otherClient = await TestClientFactory.SeedIndividualAsync(
            seed, TenantId, AgencyId, clientNumber: "ABJ-2026-000201");

        // Harvesting one field from many clients is exactly the pattern the quota exists to
        // stop, so the counter must be per USER and not per client.
        await SeedAccessLogsAsync(seed, otherClient.Id, count: 3, ageMinutes: 2);

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db, out _, out _, revealLimitPerHour: 3);

        var result = await handler.Handle(
            new RevealSensitiveFieldCommand(target.Id, SensitiveField.IdentityDocumentNumber, null),
            CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.RevealRateLimitExceeded);
    }

    [Fact]
    public async Task Fails_with_client_not_found_outside_the_agency_perimeter()
    {
        await using var seed = _factory.CreateContext();
        var client = await TestClientFactory.SeedIndividualAsync(
            seed, TenantId, OtherAgencyId, identityDocumentNumber: DocumentNumber);

        await using var db = _factory.CreateContext();
        var handler = BuildHandler(db, out _, out _, accessibleAgencies: [AgencyId]);

        var result = await handler.Handle(
            new RevealSensitiveFieldCommand(client.Id, SensitiveField.IdentityDocumentNumber, null),
            CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientNotFound);
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static async Task SeedAccessLogsAsync(
        CustomersDbContext db, Guid clientId, int count, int ageMinutes)
    {
        for (var i = 0; i < count; i++)
        {
            db.SensitiveDataAccessLogs.Add(SensitiveDataAccessLog.Record(
                tenantId: TenantId,
                clientId: clientId,
                actorUserId: ActorId,
                fieldName: nameof(SensitiveField.Phone),
                accessedAt: DateTimeOffset.UtcNow.AddMinutes(-ageMinutes),
                correlationId: null));
        }

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static RevealSensitiveFieldHandler BuildHandler(
        CustomersDbContext db,
        out ClientsTestHarness.CountingFieldEncryptor encryptor,
        out INotificationsModule notifications,
        int revealLimitPerHour = 20,
        (string Key, string Value)[]? settingOverrides = null,
        params Guid[] accessibleAgencies)
    {
        encryptor = new ClientsTestHarness.CountingFieldEncryptor(TestDoubles.Encryptor());
        notifications = Substitute.For<INotificationsModule>();
        notifications
            .QueueEmailAsync(Arg.Any<QueueEmailRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok(Guid.NewGuid()));

        var overrides = new List<(string Key, string Value)>
        {
            (CustomerSettingKeys.RevealLimitPerHour, revealLimitPerHour.ToString(
                System.Globalization.CultureInfo.InvariantCulture)),
        };
        if (settingOverrides is not null) overrides.AddRange(settingOverrides);

        return new RevealSensitiveFieldHandler(
            db,
            TestDoubles.CurrentUser(TenantId, ActorId),
            TestDoubles.AgencyScope(accessibleAgencies),
            TestDoubles.Settings(TenantId, [.. overrides]),
            encryptor,
            notifications,
            new OutboxEventPublisher<CustomersDbContext>(db),
            NullLogger<RevealSensitiveFieldHandler>.Instance);
    }
}
