namespace Sankore.Modules.Leads.Tests.Features.LeadSources;

using System.Text.Json;
using FluentAssertions;
using Sankore.Modules.Leads.Domain;
using Xunit;

/// <summary>
/// The settings editor's payload, through the real converter, and the v2 rows it has to keep
/// working.
///
/// <para>
/// <b>Why this suite exists.</b> These records declared a flat shape nobody wrote. The editor
/// posts nested blocks — <c>script</c>, <c>hostedForm</c>, <c>pull</c>, <c>consent</c> — and
/// System.Text.Json drops unmapped members, so a tenant who configured a captcha, a hosted form,
/// a provider API or an IP allow-list got "Enregistré" and lost the lot, with no error on either
/// side and no test that could see it: every existing case constructed the C# record directly,
/// which is the one thing production never does. Each case here starts from the editor's own JSON.
/// </para>
/// </summary>
public sealed class SourceSettingsEditorShapeTests
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static T Read<T>(string json) where T : SourceSettings
        => JsonSerializer.Deserialize<T>(SourceSettingsUpgrader.MigrateJson(json), Opts)!;

    // ── What the editor posts ───────────────────────────────────────────

    [Fact]
    public void An_embedded_script_payload_keeps_its_script_and_hosted_form()
    {
        var settings = Read<EmbeddedScriptSettings>("""
        {
          "$mode": "EmbeddedScript",
          "schemaVersion": 3,
          "script": {
            "allowedOrigins": ["https://imf.ci"],
            "formMode": "hosted",
            "formSelector": "#demande",
            "captchaProvider": "Turnstile",
            "captchaSiteKey": "0x4AAA",
            "honeypot": true,
            "minFillTimeSeconds": 5,
            "afterSubmit": "redirect",
            "redirectUrl": "https://imf.ci/merci",
            "preventDefaultSubmit": true
          },
          "hostedForm": {
            "fields": [
              {"name": "phoneNumber", "label": "Téléphone", "type": "tel", "isRequired": true, "options": []},
              {"name": "productCode", "label": "Produit", "type": "select", "isRequired": false, "options": ["EPARGNE", "CREDIT"]}
            ],
            "consentText": "J'accepte d'être contacté(e).",
            "consentVersion": "v2",
            "submitLabel": "Envoyer ma demande"
          },
          "consent": {"policy": "CollectedByForm", "consentFieldPath": "$.consent", "consentTextVersion": "v2"},
          "fieldMappings": [
            {"sourceField": "phoneNumber", "targetField": "phoneNumber", "transformation": "e164", "e164Country": "CI"}
          ]
        }
        """);

        settings.Script!.AllowedOrigins.Should().ContainSingle().Which.Should().Be("https://imf.ci");
        settings.Script.FormMode.Should().Be(ScriptFormMode.Hosted);
        settings.Script.CaptchaProvider.Should().Be(CaptchaProvider.Turnstile);
        settings.Script.CaptchaSiteKey.Should().Be("0x4AAA");
        settings.Script.MinFillTimeSeconds.Should().Be(5);
        settings.Script.AfterSubmit.Should().Be(ScriptAfterSubmit.Redirect);

        settings.HostedForm!.Fields.Should().HaveCount(2);
        settings.HostedForm.Fields[1].Type.Should().Be(HostedFieldType.Select);
        settings.HostedForm.Fields[1].Options.Should().Equal("EPARGNE", "CREDIT");
        settings.HostedForm.SubmitLabel.Should().Be("Envoyer ma demande");

        settings.Consent!.Policy.Should().Be(ConsentPolicy.CollectedByForm);

        // On the BASE now. Declared per-subtype and omitted here, it was accepted and dropped
        // for a web-form source while the identical screen persisted it for a webhook.
        settings.FieldMappings.Should().ContainSingle()
            .Which.Transformation.Should().Be(FieldTransformation.E164);
    }

    [Fact]
    public void An_embedded_script_payload_feeds_the_public_form_and_ingest_endpoints()
    {
        var settings = Read<EmbeddedScriptSettings>("""
        {
          "$mode": "EmbeddedScript",
          "script": {"allowedOrigins": ["https://imf.ci"], "formSelector": "#f",
                     "captchaProvider": "HCaptcha", "honeypot": true, "minFillTimeSeconds": 4},
          "hostedForm": {"fields": [{"name": "phoneNumber", "label": "Tel", "type": "tel", "isRequired": true}],
                         "consentText": "ok", "consentVersion": "v1", "submitLabel": "Go"}
        }
        """);

        // The projections the CORS check, the ping, the ingest guard and the form endpoint read.
        settings.AllowedOrigins.Should().Equal("https://imf.ci");
        settings.FormContainerId.Should().Be("#f");
        settings.CaptchaProviderName.Should().Be("HCaptcha");
        settings.MinSubmitDelaySeconds.Should().Be(4);
        settings.HoneypotFieldName.Should().Be(EmbeddedScriptSettings.DefaultHoneypotFieldName);
        settings.ConsentText.Should().Be("ok");
        settings.ConsentVersion.Should().Be("v1");
        settings.SubmitButtonLabel.Should().Be("Go");

        // Order is the array position: the editor expresses it that way and the endpoint sorts on it.
        settings.FormFields.Should().ContainSingle();
        settings.FormFields![0].Order.Should().Be(1);
        settings.FormFields[0].Type.Should().Be("tel", "the SDK reads it straight into a DOM input type");
    }

    [Fact]
    public void A_webhook_payload_keeps_the_ip_allow_list_the_editor_names()
    {
        var settings = Read<ServerWebhookSettings>("""
        {
          "$mode": "ServerWebhook",
          "allowedIps": ["41.66.0.1", "41.66.0.2"],
          "externalIdPath": "$.id",
          "consent": {"policy": "ProviderAttested", "providerContractRef": "CT-2026-07"}
        }
        """);

        // The one dropped field that was a security control turning itself off: the record said
        // AllowedIpAddresses, the editor writes allowedIps, and the endpoint went on accepting
        // pushes from any address.
        settings.AllowedIps.Should().HaveCount(2);
        settings.ExternalIdPath.Should().Be("$.id");
        settings.Consent!.Policy.Should().Be(ConsentPolicy.ProviderAttested);
        settings.Consent.ProviderContractRef.Should().Be("CT-2026-07");
    }

    [Fact]
    public void A_scheduled_pull_payload_keeps_the_whole_provider_api()
    {
        var settings = Read<ScheduledPullSettings>("""
        {
          "$mode": "ScheduledPull",
          "pull": {
            "baseUrl": "https://api.partenaire.ci",
            "authType": "ApiKey",
            "authHeaderName": "X-API-Key",
            "authHeaderLocation": "query",
            "requestMethod": "POST",
            "requestPath": "/v1/leads",
            "requestParams": "status=new",
            "paginationStrategy": "Cursor",
            "cursorJsonPath": "$.next",
            "dataJsonPath": "$.data",
            "idJsonPath": "$.id",
            "cronExpression": "*/15 * * * *",
            "schedulePreset": "15min",
            "dailyHour": 6,
            "ackEnabled": true,
            "ackMethod": "PATCH",
            "ackPath": "/v1/leads/ack",
            "costPerLead": 250.0,
            "costCurrency": "XOF"
          }
        }
        """);

        settings.Pull!.BaseUrl.Should().Be("https://api.partenaire.ci");
        settings.Pull.AckMethod.Should().Be(AckHttpMethod.Patch);
        settings.Pull.DailyHour.Should().Be(6);
        settings.Pull.CostPerLead.Should().Be(250.0m);

        // The projections the puller and the orchestrator consume.
        settings.EndpointUrl.Should().Be("https://api.partenaire.ci/v1/leads?status=new");
        settings.HttpMethod.Should().Be("POST");
        settings.CronSchedule.Should().Be("*/15 * * * *");
        settings.ItemsPath.Should().Be("$.data");
        settings.ExternalIdPath.Should().Be("$.id");
        settings.CursorPath.Should().Be("$.next");
        settings.Pagination.Should().Be(PullPaginationStrategy.Cursor);

        // The editor says "an API key, in the query string"; the puller distinguishes the two
        // placements as separate members and reads the name from the matching property only.
        settings.AuthType.Should().Be(PullAuthType.ApiKeyQuery);
        settings.AuthQueryParamName.Should().Be("X-API-Key");
        settings.AuthHeaderName.Should().BeNull();
    }

    [Theory]
    [InlineData("https://api.ci/", "/v1/leads", "", "https://api.ci/v1/leads")]
    [InlineData("https://api.ci", "v1/leads", "", "https://api.ci/v1/leads")]
    [InlineData("https://api.ci", "", "", "https://api.ci")]
    [InlineData("https://api.ci", "/leads", "?page={{page}}", "https://api.ci/leads?page={{page}}")]
    [InlineData("https://api.ci/leads?tenant=1", "", "status=new", "https://api.ci/leads?tenant=1&status=new")]
    public void The_endpoint_url_composes_the_editors_three_fields(
        string baseUrl, string path, string query, string expected)
    {
        // A double slash changes the route on plenty of gateways, and the editor accepts a
        // trailing slash on the base as readily as a leading one on the path.
        var settings = new ScheduledPullSettings
        {
            Pull = new PullConfig { BaseUrl = baseUrl, RequestPath = path, RequestParams = query },
        };

        settings.EndpointUrl.Should().Be(expected);
    }

    [Fact]
    public void A_pull_with_no_cron_falls_back_rather_than_never_running()
    {
        // The editor's own default is an empty cron. CrontabSchedule.Parse throws on blank and
        // the orchestrator catches it as "not due", so an empty expression used to mean a source
        // that silently never pulled again.
        var settings = new ScheduledPullSettings { Pull = new PullConfig { CronExpression = "" } };

        settings.CronSchedule.Should().Be(ScheduledPullSettings.DefaultCronSchedule);
    }

    [Fact]
    public void The_oauth_fields_round_trip_under_the_key_the_editor_reads()
    {
        // "OAuthTokenUrl" would camelCase to "oAuthTokenUrl" — the policy stops at the uppercase
        // letter before a lowercase one — while the editor writes "oauthTokenUrl". Reads
        // tolerated both, so only the round trip broke and the editor's OAuth tab reopened empty.
        var settings = Read<ScheduledPullSettings>("""
        {
          "$mode": "ScheduledPull",
          "pull": {"authType": "OAuthClientCredentials", "oauthTokenUrl": "https://id.ci/token",
                   "oauthClientId": "sankore", "oauthScope": "leads.read"}
        }
        """);

        settings.Pull!.OauthTokenUrl.Should().Be("https://id.ci/token");
        settings.AuthType.Should().Be(PullAuthType.OAuthClientCredentials);
        settings.OAuthClientId.Should().Be("sankore");
        settings.OAuthScopes.Should().Be("leads.read");

        var json = JsonSerializer.Serialize(settings, Opts);
        json.Should().Contain("oauthTokenUrl").And.NotContain("oAuthTokenUrl");
    }

    [Fact]
    public void Default_product_code_survives_the_round_trip()
    {
        // No column for it yet, so the creation screen parks it in the settings bag and said so
        // in its own comment. Parked in a bag that dropped it.
        Read<InternalSettings>("""{"$mode": "Internal", "defaultProductCode": "EPARGNE-01"}""")
            .DefaultProductCode.Should().Be("EPARGNE-01");
    }

    // ── v2 rows, which must keep working ────────────────────────────────

    [Fact]
    public void A_v2_embedded_script_row_migrates_into_the_editors_blocks()
    {
        var settings = Read<EmbeddedScriptSettings>("""
        {
          "$mode": "EmbeddedScript",
          "schemaVersion": 2,
          "allowedOrigins": ["https://imf.ci"],
          "formContainerId": "sankore-form",
          "redirectUrl": "https://imf.ci/merci",
          "captchaProvider": "turnstile",
          "minSubmitDelaySeconds": 7,
          "honeypotFieldName": "_hp",
          "submitButtonLabel": "Envoyer",
          "consentText": "ok",
          "consentVersion": "v1",
          "theme": "dark",
          "formFields": [{"name": "phoneNumber", "label": "Tel", "type": "tel", "isRequired": true, "order": 1}]
        }
        """);

        settings.Script!.AllowedOrigins.Should().Equal("https://imf.ci");
        settings.Script.FormSelector.Should().Be("sankore-form");
        settings.Script.MinFillTimeSeconds.Should().Be(7);
        settings.Script.CaptchaProvider.Should().Be(CaptchaProvider.Turnstile);

        // v2 expressed these two as a URL's presence and a field's NAME; v3 as enum and boolean.
        settings.Script.AfterSubmit.Should().Be(ScriptAfterSubmit.Redirect);
        settings.Script.Honeypot.Should().BeTrue();

        settings.HostedForm!.Fields.Should().ContainSingle().Which.Name.Should().Be("phoneNumber");
        settings.HostedForm.SubmitLabel.Should().Be("Envoyer");
        settings.HostedForm.Theme.Should().Be("dark");

        // And the projections still answer what the endpoints read, unchanged.
        settings.AllowedOrigins.Should().Equal("https://imf.ci");
        settings.CaptchaProviderName.Should().Be("Turnstile");
        settings.MinSubmitDelaySeconds.Should().Be(7);
    }

    [Fact]
    public void A_v2_row_with_no_captcha_does_not_gain_one()
    {
        // v2 said "no captcha" with null; v3 names that case "None". Migrating null to anything
        // else would make every submission on that source fail a check it never had.
        var settings = Read<EmbeddedScriptSettings>("""
        {"$mode": "EmbeddedScript", "schemaVersion": 2,
         "allowedOrigins": ["https://imf.ci"], "captchaProvider": null, "honeypotFieldName": null}
        """);

        settings.Script!.CaptchaProvider.Should().Be(CaptchaProvider.None);
        settings.CaptchaProviderName.Should().BeNull();
        settings.Script.Honeypot.Should().BeFalse();
    }

    [Fact]
    public void A_v2_webhook_row_keeps_its_allow_list_and_its_signature_settings()
    {
        var settings = Read<ServerWebhookSettings>("""
        {
          "$mode": "ServerWebhook",
          "schemaVersion": 2,
          "allowedIpAddresses": ["41.66.0.1"],
          "externalIdPath": "$.id",
          "contentType": "application/json",
          "signatureAlgorithm": "sha256",
          "signatureHeaderName": "X-Sankore-Signature",
          "signatureCredentialVaultRef": "vault://hmac/1"
        }
        """);

        settings.AllowedIps.Should().Equal("41.66.0.1");

        // externalIdPath stays FLAT on a webhook — it is only folded into pull.idJsonPath when
        // the row proves to be a pull — and the server-owned signature block is untouched.
        settings.ExternalIdPath.Should().Be("$.id");
        settings.SignatureAlgorithm.Should().Be("sha256");
        settings.SignatureHeaderName.Should().Be("X-Sankore-Signature");
        settings.SignatureCredentialVaultRef.Should().Be("vault://hmac/1");
    }

    [Fact]
    public void A_v2_pull_row_migrates_and_keeps_every_server_owned_limit()
    {
        var settings = Read<ScheduledPullSettings>("""
        {
          "$mode": "ScheduledPull",
          "schemaVersion": 2,
          "endpointUrl": "https://api.partenaire.ci/v1/leads?status=new",
          "httpMethod": "POST",
          "cronSchedule": "*/30 * * * *",
          "authType": "ApiKeyQuery",
          "authQueryParamName": "api_key",
          "authCredentialVaultRef": "vault://pull/1",
          "itemsPath": "$.data",
          "externalIdPath": "$.id",
          "cursorPath": "$.next",
          "pagination": "Cursor",
          "totalCountPath": "$.total",
          "pageSize": 25,
          "maxPagesPerRun": 10,
          "timeoutSeconds": 45,
          "maxResponseBytes": 1048576,
          "requestBodyTemplate": "{\"since\":\"{{since}}\"}",
          "extraHeaders": {"X-Tenant": "imf-ci"}
        }
        """);

        // The whole v2 url becomes the base, so it composes back to itself byte for byte.
        settings.EndpointUrl.Should().Be("https://api.partenaire.ci/v1/leads?status=new");
        settings.HttpMethod.Should().Be("POST");
        settings.CronSchedule.Should().Be("*/30 * * * *");
        settings.ItemsPath.Should().Be("$.data");
        settings.ExternalIdPath.Should().Be("$.id");
        settings.CursorPath.Should().Be("$.next");
        settings.Pagination.Should().Be(PullPaginationStrategy.Cursor);

        // The v2 enum carried the placement; v3 splits it and the name follows the placement.
        settings.AuthType.Should().Be(PullAuthType.ApiKeyQuery);
        settings.AuthQueryParamName.Should().Be("api_key");

        // Server-owned, and the reason they are NOT inside the editor's block: it rewrites that
        // block wholesale, so nesting these would delete the caps that keep a pull from
        // hammering someone's API and the credential it authenticates with.
        settings.AuthCredentialVaultRef.Should().Be("vault://pull/1");
        settings.TotalCountPath.Should().Be("$.total");
        settings.PageSize.Should().Be(25);
        settings.MaxPagesPerRun.Should().Be(10);
        settings.TimeoutSeconds.Should().Be(45);
        settings.MaxResponseBytes.Should().Be(1048576);
        settings.RequestBodyTemplate.Should().NotBeNull();
        settings.ExtraHeaders!["X-Tenant"].Should().Be("imf-ci");
    }

    [Fact]
    public void A_v3_payload_is_not_rewritten_from_v2_leftovers()
    {
        // A row that carries both shapes keeps what the editor last wrote. Without the guard the
        // migration would rebuild the block from the stale flat keys and undo the save.
        var settings = Read<ScheduledPullSettings>("""
        {
          "$mode": "ScheduledPull",
          "pull": {"baseUrl": "https://new.ci", "cronExpression": "0 * * * *"},
          "endpointUrl": "https://stale.ci",
          "cronSchedule": "*/5 * * * *"
        }
        """);

        settings.Pull!.BaseUrl.Should().Be("https://new.ci");
        settings.CronSchedule.Should().Be("0 * * * *");
    }

    [Fact]
    public void Migration_bumps_the_schema_version_so_it_runs_once()
    {
        var migrated = SourceSettingsUpgrader.MigrateJson(
            """{"$mode": "ServerWebhook", "schemaVersion": 2, "allowedIpAddresses": ["41.66.0.1"]}""");

        using var doc = JsonDocument.Parse(migrated);
        doc.RootElement.GetProperty("schemaVersion").GetInt32()
            .Should().Be(SourceSettingsUpgrader.CurrentVersion);
    }

    [Fact]
    public void A_payload_with_nothing_to_migrate_is_returned_untouched()
    {
        const string V3 = """{"$mode":"Internal","schemaVersion":3,"defaultProductCode":"X"}""";

        SourceSettingsUpgrader.MigrateJson(V3).Should().Be(V3);
    }
}
