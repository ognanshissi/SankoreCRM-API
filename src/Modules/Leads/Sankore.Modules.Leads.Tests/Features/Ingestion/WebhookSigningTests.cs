namespace Sankore.Modules.Leads.Tests.Features.Ingestion;

using System.Globalization;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Administration.PublicApi.Forms;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.CustomFields;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Modules.Leads.Features.Ingestion;
using Sankore.Modules.Leads.Features.Ingestion.Webhook;
using Sankore.Modules.Leads.Features.LeadSources.ActivateLeadSource;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The inbound lead webhook, hardened (webhooks lot 5). A source whose secret is in the vault
/// requires a signature — it used to verify only when an algorithm was set, which no screen did,
/// so a source showing a secret accepted unsigned pushes. A rotated-out secret stops working after
/// its week. A refusal is noted on the source. A server webhook cannot go live without a secret.
/// A repeated external id may update the lead's custom fields.
/// </summary>
public sealed class WebhookSigningTests : IDisposable
{
    private const string Secret = "current-secret-value";
    private const string OldSecret = "previous-secret-value";

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly ISecretsModule _vault = Substitute.For<ISecretsModule>();
    private readonly ISender _sender = Substitute.For<ISender>();

    public WebhookSigningTests()
    {
        _factory = new TestDbContextFactory(_tenantId);
        _sender.Send(Arg.Any<IngestInboundLeadCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok(new IngestInboundLeadResult(Guid.NewGuid(), Guid.NewGuid(), LeadIngestionStatus.Accepted)));
    }

    public void Dispose() => _factory.Dispose();

    // ── Verifier ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Any_of_several_v1_signatures_may_match()
    {
        var now = DateTimeOffset.UtcNow;
        var header = Header("{}", now, "ab12", Secret);

        WebhookSignatureVerifier.Verify(header, "{}", "sha256", Secret, null, now).Should().BeNull();
        WebhookSignatureVerifier.Verify(header.Replace("v1=ab12,", ""), "{}", "sha256", Secret, null, now).Should().BeNull();
    }

    // ── Endpoint handler ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_source_with_a_secret_refuses_an_unsigned_push_and_notes_why()
    {
        var source = await SeedAsync(withSecret: true);

        var result = await Push(source, "{\"id\":\"1\"}", headers: []);

        result.StatusCode.Should().Be(401);
        await _sender.DidNotReceiveWithAnyArgs().Send(default(IngestInboundLeadCommand)!, default);

        await using var db = _factory.CreateContext();
        var stored = await db.LeadSourceConfigs.IgnoreQueryFilters().SingleAsync();
        stored.LastRejectedReason.Should().Be("SIGNATURE_MISSING");
        stored.LastRejectedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_correctly_signed_push_is_accepted()
    {
        var source = await SeedAsync(withSecret: true);
        const string body = "{\"id\":\"1\",\"phone\":\"+2250707070707\",\"name\":\"Awa\"}";

        var result = await Push(source, body, new() { ["X-Sankore-Signature"] = Header(body, DateTimeOffset.UtcNow, null, Secret) });

        result.StatusCode.Should().Be(202);
    }

    [Fact]
    public async Task The_configured_header_is_the_one_read()
    {
        var source = await SeedAsync(withSecret: true, headerName: "X-Partner-Signature");
        const string body = "{\"id\":\"1\"}";
        var signature = Header(body, DateTimeOffset.UtcNow, null, Secret);

        (await Push(source, body, new() { ["X-Sankore-Signature"] = signature })).StatusCode.Should().Be(401);
        (await Push(source, body, new() { ["x-partner-signature"] = signature })).StatusCode.Should().Be(202);
    }

    [Fact]
    public async Task A_rotated_out_secret_is_accepted_during_its_week_and_refused_after()
    {
        const string body = "{\"id\":\"1\"}";
        var source = await SeedAsync(withSecret: true, previousExpiresAt: DateTimeOffset.UtcNow.AddDays(3));
        (await Push(source, body, new() { ["X-Sankore-Signature"] = Header(body, DateTimeOffset.UtcNow, null, OldSecret) }))
            .StatusCode.Should().Be(202);

        var expired = await SeedAsync(withSecret: true, previousExpiresAt: DateTimeOffset.UtcNow.AddMinutes(-1), code: "OLD");
        (await Push(expired, body, new() { ["X-Sankore-Signature"] = Header(body, DateTimeOffset.UtcNow, null, OldSecret) }))
            .StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task A_source_without_a_secret_still_accepts_unsigned_pushes()
    {
        // Existing live sources that never generated one keep working; they simply cannot be
        // re-activated without one.
        var source = await SeedAsync(withSecret: false);

        (await Push(source, "{\"id\":\"1\"}", headers: [])).StatusCode.Should().Be(202);
    }

    [Fact]
    public async Task An_address_outside_the_allow_list_is_refused_and_noted()
    {
        var source = await SeedAsync(withSecret: false, allowedIp: "10.1.2.3");

        (await Push(source, "{\"id\":\"1\"}", headers: [])).StatusCode.Should().Be(403);

        await using var db = _factory.CreateContext();
        (await db.LeadSourceConfigs.IgnoreQueryFilters().SingleAsync()).LastRejectedReason.Should().Be("IP_NOT_ALLOWED");
    }

    // ── Activation ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_server_webhook_cannot_go_live_without_its_secret()
    {
        var source = await SeedAsync(withSecret: false, active: false);

        await using (var db = _factory.CreateContext())
        {
            var refused = await new ActivateLeadSourceHandler(db, _vault).Handle(new ActivateLeadSourceCommand(source.Id), default);
            refused.Error.Should().Contain("SecretMissing");
        }

        StoreSecret(source.Id, previousExpiresAt: null);
        await using (var db = _factory.CreateContext())
        {
            (await new ActivateLeadSourceHandler(db, _vault).Handle(new ActivateLeadSourceCommand(source.Id), default))
                .IsSuccess.Should().BeTrue();
        }
    }

    // ── Update by external id ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task A_repeated_external_id_updates_the_custom_fields_only_when_the_source_allows_it(bool allowUpdates, bool expectUpdated)
    {
        var admin = Substitute.For<IAdministrationModule>();
        var catalog = NativeFormCatalog.For(FormEntity.Lead);
        admin.GetEntityFormAsync(_tenantId, FormEntity.Lead, Arg.Any<CancellationToken>())
            .Returns(new EntityFormDto(FormEntity.Lead, 1, catalog.Sections,
            [
                .. catalog.Fields,
                new FormField("score_partenaire", FormFieldType.Text, "Score partenaire", 1, "request"),
                new FormField("canal", FormFieldType.Text, "Canal", 2, "request"),
            ]));
        var indexer = Substitute.For<IPhoneBlindIndexer>();
        indexer.Compute(Arg.Any<string>()).Returns(ci => $"idx_{ci.Arg<string>()}");

        var source = await SeedAsync(withSecret: false, allowUpdates: allowUpdates,
            mappings:
            [
                new() { SourceField = "$.phone", TargetField = "phoneNumber" },
                new() { SourceField = "$.name", TargetField = "fullName" },
                new() { SourceField = "$.score", TargetField = "custom.score_partenaire" },
                new() { SourceField = "$.canal", TargetField = "custom.canal" },
            ]);

        Guid? leadId;
        await using (var db = _factory.CreateContext())
        {
            var first = await new IngestInboundLeadHandler(db, indexer, TimeProvider.System, admin, NullLogger<IngestInboundLeadHandler>.Instance)
                .Handle(new IngestInboundLeadCommand(_tenantId, source.Id,
                    """{"id":"ext-9","phone":"+2250707070707","name":"Awa Koné","score":"B","canal":"agence"}""", "ext-9"), default);
            leadId = first.Value.LeadId;
        }

        await using (var db = _factory.CreateContext())
        {
            // A resend of the score only: no name, no phone, no canal.
            var again = await new IngestInboundLeadHandler(db, indexer, TimeProvider.System, admin, NullLogger<IngestInboundLeadHandler>.Instance)
                .Handle(new IngestInboundLeadCommand(_tenantId, source.Id, """{"id":"ext-9","score":"A"}""", "ext-9"), default);

            again.Value.LeadId.Should().Be(leadId);
            again.Value.Updated.Should().Be(expectUpdated);
        }

        await using var verify = _factory.CreateContext();
        var values = CustomFieldValues.Read((await verify.Leads.IgnoreQueryFilters().SingleAsync()).CustomFieldsJson)!;
        values["score_partenaire"].GetString().Should().Be(expectUpdated ? "A" : "B");
        values["canal"].GetString().Should().Be("agence", "an update merges, it never clears what it did not send");
    }

    // ── Plumbing ────────────────────────────────────────────────────────────────────────────

    private async Task<LeadSourceConfig> SeedAsync(
        bool withSecret, string? headerName = null, DateTimeOffset? previousExpiresAt = null, string code = "HOOK",
        string? allowedIp = null, bool active = true, bool allowUpdates = false, List<FieldMappingRule>? mappings = null)
    {
        await using var db = _factory.CreateContext();
        var settings = new ServerWebhookSettings
        {
            FieldMappings = mappings ??
            [
                new() { SourceField = "$.phone", TargetField = "phoneNumber" },
                new() { SourceField = "$.name", TargetField = "fullName" },
            ],
            ExternalIdPath = "$.id",
            SignatureHeaderName = headerName,
            AllowedIps = allowedIp is null ? [] : [allowedIp],
            AllowUpdates = allowUpdates,
        };
        var source = LeadSourceConfig.Create(_tenantId, code, "Webhook", LeadChannelType.InboundWebhook, 0,
            IntegrationMode.ServerWebhook, settings: settings);
        source.StartTesting();
        if (active) source.Activate();
        db.LeadSourceConfigs.Add(source);
        await db.SaveChangesAsync();

        if (withSecret) StoreSecret(source.Id, previousExpiresAt);
        return source;
    }

    private void StoreSecret(Guid sourceId, DateTimeOffset? previousExpiresAt)
    {
        var current = new SecretKey(_tenantId, WebhookSigning.Scope, sourceId, WebhookSigning.CurrentName);
        _vault.GetValueAsync(current, Arg.Any<CancellationToken>()).Returns(Secret);
        _vault.GetHintAsync(current, Arg.Any<CancellationToken>()).Returns(new SecretHint(WebhookSigning.CurrentName, "curr****alue", null));

        if (previousExpiresAt is { } expires)
        {
            var previous = new SecretKey(_tenantId, WebhookSigning.Scope, sourceId, WebhookSigning.PreviousName);
            _vault.GetValueAsync(previous, Arg.Any<CancellationToken>()).Returns(OldSecret);
            _vault.GetHintAsync(previous, Arg.Any<CancellationToken>()).Returns(new SecretHint(WebhookSigning.PreviousName, "prev****alue", expires));
        }
    }

    private async Task<IngestWebhookResult> Push(LeadSourceConfig source, string body, Dictionary<string, string> headers)
    {
        await using var db = _factory.CreateContext();
        var handler = new IngestWebhookHandler(_sender,
            new WebhookSourceGate(db, _vault, TimeProvider.System, NullLogger<WebhookSourceGate>.Instance));
        var result = await handler.Handle(new IngestWebhookCommand(
            source.PublicKey!, body, new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase), "203.0.113.7"), default);
        return result.Value!;
    }

    /// <summary>What a partner sends: t=…,[v1=extra,]v1=HMAC-SHA256(secret, "t.body").</summary>
    private static string Header(string body, DateTimeOffset at, string? extra, string secret)
    {
        var t = at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var v1 = WebhookSignatureVerifier.ComputeHmac("sha256", secret, $"{t}.{body}");
        return extra is null ? $"t={t},v1={v1}" : $"t={t},v1={extra},v1={v1}";
    }
}
