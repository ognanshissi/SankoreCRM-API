namespace Sankore.Modules.Integration.Tests.Features.Sync;

using System.Globalization;
using System.Text;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Integration.Features.Sync;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// INT-20 criterion 4, the endpoint's half: a verified webhook enqueues a targeted sync, and
/// everything else answers 401 with nothing in it.
///
/// <para>
/// The decision is exercised through <c>SyncWebhookEndpoints.AcceptAsync</c>, which takes no
/// <c>HttpContext</c> — this repository has no <c>WebApplicationFactory</c> precedent, and the
/// security properties under test are not HTTP properties. The status-code mapping is pinned
/// separately, on <c>ToResult</c>.
/// </para>
/// </summary>
public sealed class SyncWebhookEndpointTests
{
    private const string Secret = "a-dev-only-webhook-secret-at-least-32-bytes";

    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-7777-0000-0000-000000000001");

    private static readonly DateTimeOffset Now = SyncTestContext.Now;

    [Fact]
    public async Task A_valid_signature_is_accepted_and_enqueues_a_targeted_customer_sync()
    {
        await using var db = SyncTestContext.NewDb(TenantA);
        var connection = SyncTestContext.SeedConnection(db, TenantA);

        var body = Body("CBS-0001");
        var hangfire = Substitute.For<IBackgroundJobClient>();

        var outcome = await Accept(db, hangfire, connection.Id, body, Signed(body));

        outcome.Should().Be(WebhookOutcome.Accepted);

        var job = Jobs(hangfire).Single();

        job.Type.Should().Be<SyncCustomerJob>();

        // Never a full sweep, and nothing of the body beyond the identity it named: a tenant id, a
        // connection id and one external reference.
        job.Args.Should().HaveCount(3);
        job.Args[0].Should().Be(TenantA);
        job.Args[1].Should().Be(connection.Id);
        job.Args[2].Should().Be("CBS-0001");
    }

    [Fact]
    public async Task A_wrong_signature_is_rejected_and_enqueues_nothing()
    {
        await using var db = SyncTestContext.NewDb(TenantA);
        var connection = SyncTestContext.SeedConnection(db, TenantA);

        var body = Body("CBS-0001");
        var hangfire = Substitute.For<IBackgroundJobClient>();

        var outcome = await Accept(
            db, hangfire, connection.Id, body,
            SyncWebhookVerifier.Sign("some-other-secret-entirely", Unix(Now), body));

        outcome.Should().Be(WebhookOutcome.Rejected);
        hangfire.DidNotReceiveWithAnyArgs().Create(default!, default!);
    }

    [Fact]
    public async Task A_replayed_timestamp_is_refused()
    {
        await using var db = SyncTestContext.NewDb(TenantA);
        var connection = SyncTestContext.SeedConnection(db, TenantA);

        var body = Body("CBS-0001");
        var hangfire = Substitute.For<IBackgroundJobClient>();

        // An authentic request minted twenty minutes ago, re-sent now against a five-minute
        // window. The bytes and the signature are genuine; only the clock refuses it.
        var mintedAt = Now.AddMinutes(-20);

        var outcome = await Accept(
            db, hangfire, connection.Id, body,
            SyncWebhookVerifier.Sign(Secret, Unix(mintedAt), body),
            timestamp: Unix(mintedAt));

        outcome.Should().Be(WebhookOutcome.Rejected);
        hangfire.DidNotReceiveWithAnyArgs().Create(default!, default!);
    }

    [Fact]
    public async Task An_unknown_connection_is_rejected_exactly_like_a_bad_signature()
    {
        // 401 and not 404: a 404 would make this endpoint a connection-enumeration oracle, and the
        // list of connection ids that exist is an inventory of who our customers integrate with.
        await using var db = SyncTestContext.NewDb(TenantA);
        SyncTestContext.SeedConnection(db, TenantA);

        var body = Body("CBS-0001");
        var hangfire = Substitute.For<IBackgroundJobClient>();
        var secrets = Secrets();

        var outcome = await Accept(
            db, hangfire, Guid.NewGuid(), body, Signed(body), secrets: secrets);

        outcome.Should().Be(WebhookOutcome.Rejected);
        hangfire.DidNotReceiveWithAnyArgs().Create(default!, default!);

        // And the vault was never consulted: there is no tenant to key a secret by, so nothing
        // about the unknown id reaches further into the system than the first query.
        await secrets.DidNotReceiveWithAnyArgs().GetValueAsync(default!, default);
    }

    [Fact]
    public async Task A_deactivated_connection_is_rejected_exactly_like_an_unknown_one()
    {
        // Deactivating a connection must stop its webhook URL working, and answering differently
        // from an unknown id would confirm that the connection exists.
        await using var db = SyncTestContext.NewDb(TenantA);
        var connection = SyncTestContext.SeedConnection(db, TenantA, active: false);

        var body = Body("CBS-0001");
        var hangfire = Substitute.For<IBackgroundJobClient>();

        var outcome = await Accept(db, hangfire, connection.Id, body, Signed(body));

        outcome.Should().Be(WebhookOutcome.Rejected);
        hangfire.DidNotReceiveWithAnyArgs().Create(default!, default!);
    }

    [Fact]
    public async Task A_connection_with_no_webhook_secret_is_rejected()
    {
        // Fail closed. A connection whose callers cannot be authenticated must not become an
        // unauthenticated write path into the sync queue.
        await using var db = SyncTestContext.NewDb(TenantA);
        var connection = SyncTestContext.SeedConnection(db, TenantA);

        var body = Body("CBS-0001");
        var hangfire = Substitute.For<IBackgroundJobClient>();

        var outcome = await Accept(
            db, hangfire, connection.Id, body, Signed(body), secrets: Secrets(stored: null));

        outcome.Should().Be(WebhookOutcome.Rejected);
        hangfire.DidNotReceiveWithAnyArgs().Create(default!, default!);
    }

    [Fact]
    public async Task The_secret_is_read_under_the_tenant_the_connection_belongs_to()
    {
        // The request carries no JWT, so the tenant can only come from the row. A secret keyed by
        // anything else would either never be found or — worse — be another tenant's.
        await using var db = SyncTestContext.NewDb(TenantA);
        var connection = SyncTestContext.SeedConnection(db, TenantA);

        var body = Body("CBS-0001");
        var secrets = Secrets();

        await Accept(
            db, Substitute.For<IBackgroundJobClient>(), connection.Id, body, Signed(body),
            secrets: secrets);

        await secrets.Received(1).GetValueAsync(
            Arg.Is<SecretKey>(k =>
                k.TenantId == TenantA
                && k.EntityId == connection.Id
                && k.Scope == "integration"
                && k.Name == "webhook-secret"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_verified_body_that_names_no_external_identifier_is_accepted_and_ignored()
    {
        // The signature already proved the sender; a 4xx would only make it redeliver the same
        // body for ever. Nothing is enqueued, and the warning is where a field-name mismatch with
        // a real installation will show up.
        await using var db = SyncTestContext.NewDb(TenantA);
        var connection = SyncTestContext.SeedConnection(db, TenantA);

        var body = Encoding.UTF8.GetBytes("""{"entityType":"Customer"}""");
        var hangfire = Substitute.For<IBackgroundJobClient>();

        var outcome = await Accept(db, hangfire, connection.Id, body, Signed(body));

        outcome.Should().Be(WebhookOutcome.Accepted);
        hangfire.DidNotReceiveWithAnyArgs().Create(default!, default!);
    }

    [Fact]
    public async Task A_verified_body_that_is_not_readable_json_is_accepted_and_ignored()
    {
        await using var db = SyncTestContext.NewDb(TenantA);
        var connection = SyncTestContext.SeedConnection(db, TenantA);

        var body = Encoding.UTF8.GetBytes("this is not json at all");
        var hangfire = Substitute.For<IBackgroundJobClient>();

        var outcome = await Accept(db, hangfire, connection.Id, body, Signed(body));

        outcome.Should().Be(WebhookOutcome.Accepted);
        hangfire.DidNotReceiveWithAnyArgs().Create(default!, default!);
    }

    [Fact]
    public async Task A_verified_body_naming_another_entity_type_is_accepted_and_ignored()
    {
        // Criterion 4 is about the customer. A sender that emits several event families needs no
        // per-family configuration on its side to stop being told 401 by ours.
        await using var db = SyncTestContext.NewDb(TenantA);
        var connection = SyncTestContext.SeedConnection(db, TenantA);

        var body = Encoding.UTF8.GetBytes(
            """{"entityType":"Loan","externalId":"LOAN-0001"}""");
        var hangfire = Substitute.For<IBackgroundJobClient>();

        var outcome = await Accept(db, hangfire, connection.Id, body, Signed(body));

        outcome.Should().Be(WebhookOutcome.Accepted);
        hangfire.DidNotReceiveWithAnyArgs().Create(default!, default!);
    }

    [Fact]
    public async Task An_envelope_with_no_entity_type_is_read_as_a_customer()
    {
        await using var db = SyncTestContext.NewDb(TenantA);
        var connection = SyncTestContext.SeedConnection(db, TenantA);

        var body = Encoding.UTF8.GetBytes("""{"externalId":"CBS-0001"}""");
        var hangfire = Substitute.For<IBackgroundJobClient>();

        var outcome = await Accept(db, hangfire, connection.Id, body, Signed(body));

        outcome.Should().Be(WebhookOutcome.Accepted);
        Jobs(hangfire).Single().Args[2].Should().Be("CBS-0001");
    }

    [Fact]
    public void The_answer_is_204_or_401_and_carries_no_body()
    {
        // The whole of "no detail about which check failed". Two statuses, both with an empty
        // body — and the outcome type has exactly two values, so there is no reason code anywhere
        // for a future edit to return by accident.
        Enum.GetValues<WebhookOutcome>().Should().HaveCount(2);

        SyncWebhookEndpoints.ToResult(WebhookOutcome.Accepted)
            .Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status204NoContent);

        SyncWebhookEndpoints.ToResult(WebhookOutcome.Rejected)
            .Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);

        SyncWebhookEndpoints.ToResult(WebhookOutcome.Rejected)
            .Should().NotBeAssignableTo<IValueHttpResult>();
    }

    private static byte[] Body(string externalId)
        => Encoding.UTF8.GetBytes(
            $$"""{"entityType":"Customer","externalId":"{{externalId}}"}""");

    private static string Signed(byte[] body) => SyncWebhookVerifier.Sign(Secret, Unix(Now), body);

    private static string Unix(DateTimeOffset at)
        => at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    private static ISecretsModule Secrets(string? stored = Secret)
    {
        var secrets = Substitute.For<ISecretsModule>();
        secrets.GetValueAsync(Arg.Any<SecretKey>(), Arg.Any<CancellationToken>()).Returns(stored);
        return secrets;
    }

    private static Task<WebhookOutcome> Accept(
        IntegrationDbContext db,
        IBackgroundJobClient hangfire,
        Guid connectionId,
        byte[] body,
        string? signature,
        string? timestamp = null,
        ISecretsModule? secrets = null)
        => SyncWebhookEndpoints.AcceptAsync(
            connectionId,
            body,
            timestamp ?? Unix(Now),
            signature,
            db,
            secrets ?? Secrets(),
            hangfire,
            new SyncTestContext.FixedClock(Now),
            new IntegrationWebhookOptions(),
            NullLogger<SyncWebhookEndpointTests>.Instance,
            CancellationToken.None);

    private static IReadOnlyList<Job> Jobs(IBackgroundJobClient hangfire)
        => hangfire.ReceivedCalls()
            .Select(c => c.GetArguments()[0])
            .OfType<Job>()
            .Where(j => j.Type == typeof(SyncCustomerJob))
            .ToList();
}
