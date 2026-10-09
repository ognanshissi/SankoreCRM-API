namespace Sankore.Modules.Integration.Tests.Adapters.Temenos;

using System.Net;
using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Mappings;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;
using Polly.CircuitBreaker;
using Xunit;

/// <summary>
/// What the Temenos adapter does that the shared contract suites cannot ask it (INT-12, INT-13).
///
/// <para>
/// The contract suites assert what every adapter must do. The facts below are about THIS one: the
/// pre-create search of INT-12's third criterion, the capability matrix, the mapping failure of
/// INT-13's second criterion, the call-journal row, and what actually reaches the wire. Each of
/// them is a claim the contract suites are structurally unable to make, because they are written
/// against a port and not against an installation.
/// </para>
/// </summary>
public sealed class TemenosAdapterTests
{
    private static IdempotencyKey Key(string scenario) => new($"temenos-{scenario}");

    private static CbsCustomerPayload Payload(string reference = "CRM-000123")
        => TestSupport.FakeAdapterFixtures.CustomerPayload() with { CrmReference = reference };

    // ── Capabilities ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_capability_matrix_declares_the_eight_operations_of_INT_12_and_INT_13()
    {
        using var harness = TemenosTestHarness.Create();

        // The connection the harness seeded: CapabilitiesFor is asked about a row, and this
        // adapter's matrix is the same for every one of them — a tenant has at most one active
        // core-banking connection (ux_integration_connection_active_core_banking).
        var modes = harness.Adapter.CapabilitiesFor(harness.Connection).Modes;

        modes.Keys.Should().BeEquivalentTo(new[]
        {
            IntegrationCapability.CreateCustomer,
            IntegrationCapability.UpdateCustomer,
            IntegrationCapability.SetKycLevel,
            IntegrationCapability.OpenAccount,
            IntegrationCapability.ReadAccounts,
            IntegrationCapability.ReadBalance,
            IntegrationCapability.ReadTransactions,
            IntegrationCapability.ReadMonthlyFlow,
        });

        modes.Values.Should().AllBeEquivalentTo(CapabilityMode.RealTime,
            "Transact answers synchronously over HTTP; declaring batch would make the platform "
            + "wait for an acknowledgement nothing will ever produce");
    }

    [Fact]
    public void The_premium_debit_is_absent_from_the_matrix_so_a_screen_hides_the_button()
    {
        using var harness = TemenosTestHarness.Create();

        var matrix = harness.Adapter.CapabilitiesFor(harness.Connection);

        // ASS-05 is not part of INT-13. A capability that is absent from the map is not
        // supported, which is what IntegrationAdapterResolver.ResolvePort reads before calling.
        matrix.Supports(IntegrationCapability.DebitAccount).Should().BeFalse();
        matrix.Supports(IntegrationCapability.ReverseDebit).Should().BeFalse();
        matrix.ModeOf(IntegrationCapability.DebitAccount).Should().BeNull();
    }

    [Fact]
    public async Task A_premium_debit_is_refused_as_unsupported_and_never_reaches_the_installation()
    {
        using var harness = TemenosTestHarness.Create();

        var debit = await harness.Adapter.DebitAccountAsync(
            new ExternalId(TemenosFixtures.SeededAccountId), 1_000m, "XOF", "Prime",
            Key("debit"), CancellationToken.None);

        var reversal = await harness.Adapter.ReverseDebitAsync(
            new ExternalId(TemenosFixtures.SeededAccountId), "REF", Key("reverse"), CancellationToken.None);

        debit.Family.Should().Be(ErrorFamily.Technical);
        debit.Code.Should().Be(IntegrationErrors.CapabilityNotSupported);
        reversal.Code.Should().Be(IntegrationErrors.CapabilityNotSupported);

        harness.Transport.ApiCalls.Should().Be(0, "an unsupported operation must not open a socket");
    }

    // ── INT-12, criterion 3: the pre-create search ──────────────────────────────────────────

    [Fact]
    public async Task A_creation_searches_by_the_CRM_reference_before_it_creates()
    {
        using var harness = TemenosTestHarness.Create();

        await harness.Adapter.CreateCustomerAsync(Payload("CRM-SEARCH-FIRST"), Key("search"), CancellationToken.None);

        var first = harness.Transport.Requests[0];

        first.Method.Should().Be(HttpMethod.Get);
        first.Uri.Query.Should().Contain("mnemonic=CRM-SEARCH-FIRST",
            "the search is what makes a retried creation safe, and it searches by OUR reference");
    }

    [Fact]
    public async Task A_second_creation_of_one_customer_returns_the_first_reference_and_creates_nothing()
    {
        using var harness = TemenosTestHarness.Create();
        var payload = Payload("CRM-AMBIGUOUS-TIMEOUT");

        // Two DIFFERENT keys, on purpose: this is the ambiguous-timeout case, where the first
        // attempt succeeded at the installation and we never saw the answer, so the retry is
        // computed afresh. The key cannot save us; only the search can.
        var first = await harness.Adapter.CreateCustomerAsync(payload, Key("attempt-1"), CancellationToken.None);
        var second = await harness.Adapter.CreateCustomerAsync(payload, Key("attempt-2"), CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        second.Value.Should().Be(first.Value,
            "a second party for one person is a duplicate nothing reconciles — INT-34 cannot tell "
            + "one we made from one the IMF made at a counter");

        harness.Transport.CustomerPosts.Should().Be(1, "the second attempt must not reach a POST");
    }

    [Fact]
    public async Task A_search_that_failed_does_not_create_because_we_do_not_know()
    {
        using var harness = TemenosTestHarness.Create();
        harness.Transport.ForcedSearchAnswer = (HttpStatusCode.ServiceUnavailable, TemenosFixtures.MaintenanceError);

        var result = await harness.Adapter.CreateCustomerAsync(
            Payload("CRM-UNKNOWN-STATE"), Key("blind"), CancellationToken.None);

        result.Family.Should().Be(ErrorFamily.Transient,
            "we learned nothing, so the dispatcher must come back rather than park the command");

        // The whole point of the criterion: creating on "do not know" is the duplicate.
        harness.Transport.CustomerPosts.Should().Be(0);
    }

    [Fact]
    public async Task Two_parties_under_one_CRM_reference_are_a_functional_duplicate_and_not_a_third_party()
    {
        using var harness = TemenosTestHarness.Create();
        harness.Transport.ForcedSearchAnswer = (
            HttpStatusCode.OK,
            TemenosFixtures.CustomerSearchTwoHits.Replace("{0}", "300001").Replace("{1}", "300002"));

        var result = await harness.Adapter.CreateCustomerAsync(
            Payload("DOUBLE"), Key("double"), CancellationToken.None);

        result.Family.Should().Be(ErrorFamily.Functional);
        result.Code.Should().Be(IntegrationErrors.Duplicate);
        result.IsRetryable.Should().BeFalse("a human must say which of the two is the customer's");
        harness.Transport.CustomerPosts.Should().Be(0);
    }

    [Fact]
    public async Task A_creation_that_does_not_name_the_party_is_refused_rather_than_stored_empty()
    {
        using var harness = TemenosTestHarness.Create();
        harness.Transport.ForcedCreateAnswer = (HttpStatusCode.OK, TemenosFixtures.CustomerCreatedWithoutId);

        var result = await harness.Adapter.CreateCustomerAsync(
            Payload("CRM-NO-ID"), Key("no-id"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Code.Should().Be(IntegrationErrors.UnexpectedResponse,
            "an empty ExternalId in integration_reference reads back later as a live link to nothing");
    }

    [Fact]
    public async Task A_payload_with_no_CRM_reference_is_searchable_by_the_CRM_customer_id()
    {
        using var harness = TemenosTestHarness.Create();
        var payload = TestSupport.FakeAdapterFixtures.CustomerPayload() with { CrmReference = null };

        await harness.Adapter.CreateCustomerAsync(payload, Key("fallback"), CancellationToken.None);

        harness.Transport.Requests[0].Uri.Query.Should().Contain(
            payload.CrmCustomerId.ToString(),
            "the search key must be derivable from the customer on every attempt, so it falls back "
            + "to the one identifier a CRM customer always has");
    }

    // ── INT-13, criterion 2: the product mapping ────────────────────────────────────────────

    [Fact]
    public async Task An_unmapped_product_code_is_technical_and_names_the_domain_and_the_code()
    {
        using var harness = TemenosTestHarness.Create();

        var result = await harness.Adapter.OpenAccountAsync(
            new ExternalId(TemenosFixtures.SeededCustomerId), "PRET-SCOLAIRE",
            Key("unmapped"), CancellationToken.None);

        result.Family.Should().Be(ErrorFamily.Technical,
            "the misconfiguration is ours: no amount of retrying fills a mapping table, and the "
            + "family decides whether an administrator is woken or a clerk handed a rejection");
        result.Code.Should().Be(IntegrationErrors.MappingMissing);
        result.Detail.Should().Be(MappingResolver.MissingDetail(MappingDomain.Product, "PRET-SCOLAIRE"));
        result.Detail.Should().Contain("Product").And.Contain("PRET-SCOLAIRE");

        harness.Transport.ApiCalls.Should().Be(0,
            "a code that cannot be translated has nothing to send, and the call must not be made");
    }

    [Fact]
    public async Task An_empty_product_code_is_refused_with_the_same_error_as_an_unmapped_one()
    {
        using var harness = TemenosTestHarness.Create();

        var result = await harness.Adapter.OpenAccountAsync(
            new ExternalId(TemenosFixtures.SeededCustomerId), "   ", Key("blank"), CancellationToken.None);

        // "An absent code produces a technical error" covers the caller that passed nothing as
        // much as the tenant that configured nothing.
        result.Family.Should().Be(ErrorFamily.Technical);
        result.Code.Should().Be(IntegrationErrors.MappingMissing);
    }

    [Fact]
    public async Task An_unmapped_customer_code_refuses_the_creation_rather_than_passing_a_CRM_code_through()
    {
        // No mapping rows at all: every code field of the payload is then untranslatable, and the
        // first one refuses. Sending an unmapped code is how a customer is created at the CBS with
        // a profession nobody can read, and the row looks successful for ever (INT-04).
        using var harness = TemenosTestHarness.Create(withMappings: false);

        var result = await harness.Adapter.CreateCustomerAsync(
            Payload("CRM-NO-MAPPINGS"), Key("no-mappings"), CancellationToken.None);

        result.Family.Should().Be(ErrorFamily.Technical);
        result.Code.Should().Be(IntegrationErrors.MappingMissing);
        harness.Transport.CustomerPosts.Should().Be(0);
    }

    [Fact]
    public async Task A_listed_account_carries_the_CRM_product_code_and_not_the_external_one()
    {
        using var harness = TemenosTestHarness.Create();

        var accounts = await harness.Adapter.GetAccountsAsync(
            new ExternalId(TemenosFixtures.SeededCustomerId), CancellationToken.None);

        accounts.IsSuccess.Should().BeTrue();
        accounts.Value.Should().ContainSingle()
            .Which.ProductCode.Should().Be(TemenosFixtures.SeededCrmProductCode,
                "Customer 360 speaks the CRM's vocabulary; the reverse translation is what lets it");
    }

    [Fact]
    public async Task An_account_whose_product_is_unmapped_still_lists_with_the_external_code()
    {
        // The historic portfolio: accounts the IMF opened before SANKORE existed carry products
        // the mapping table has never heard of. Abandoning the list would empty Customer 360 for
        // exactly the customers with the longest history.
        using var harness = TemenosTestHarness.Create(withMappings: false);

        var accounts = await harness.Adapter.GetAccountsAsync(
            new ExternalId(TemenosFixtures.SeededCustomerId), CancellationToken.None);

        accounts.IsSuccess.Should().BeTrue();
        accounts.Value.Should().ContainSingle()
            .Which.ProductCode.Should().Be(TemenosFixtures.SeededExternalProductCode);
    }

    // ── Reads ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_balance_reads_the_booked_figure_and_the_available_one_separately()
    {
        using var harness = TemenosTestHarness.Create();

        var balance = await harness.Adapter.GetBalanceAsync(
            new ExternalId(TemenosFixtures.SeededAccountId), CancellationToken.None);

        balance.IsSuccess.Should().BeTrue();

        // The fixture reports the booked balance as a JSON NUMBER and the available one as a
        // STRING, which is what installations actually do — see TemenosFixtures.
        balance.Value.Balance.Should().Be(125_000m);
        balance.Value.AvailableBalance.Should().Be(120_000m);
        balance.Value.Currency.Should().Be(TemenosFixtures.SeededCurrency);
        balance.Value.IsStale.Should().BeFalse("this IS the live call; the stale flag is INT-15's");
    }

    [Fact]
    public async Task A_transaction_read_in_T24_native_date_format_lands_on_the_right_day()
    {
        using var harness = TemenosTestHarness.Create();

        var page = await harness.Adapter.GetTransactionsAsync(
            new ExternalId(TemenosFixtures.SeededAccountId),
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31), null, CancellationToken.None);

        page.IsSuccess.Should().BeTrue();

        // The fixture emits yyyyMMdd, T24's native form. A parser that read it with the process
        // culture would land 20240105 on another day, or refuse it — the exact fault the
        // spreadsheet importers of this repository were bitten by.
        page.Value.Items.Should().Contain(t => t.Reference == "TRX-0001"
                                               && t.ValueDate == new DateOnly(2024, 1, 5)
                                               && t.Direction == "Credit"
                                               && t.Amount == 50_000m);
    }

    [Fact]
    public async Task The_pager_walks_every_movement_once_and_ends_on_a_null_cursor()
    {
        using var harness = TemenosTestHarness.Create();
        var seen = new List<string>();
        string? cursor = null;

        do
        {
            var page = await harness.Adapter.GetTransactionsAsync(
                new ExternalId(TemenosFixtures.SeededAccountId),
                new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31), cursor, CancellationToken.None);

            page.IsSuccess.Should().BeTrue();
            seen.AddRange(page.Value.Items.Select(t => t.Reference));
            cursor = page.Value.NextCursor;
        }
        while (cursor is not null);

        seen.Should().HaveCount(5).And.OnlyHaveUniqueItems(
            "a page boundary that re-serves a movement double-counts it in the flow ceiling");
    }

    [Fact]
    public async Task A_monthly_flow_totals_both_directions_and_never_nets_them()
    {
        using var harness = TemenosTestHarness.Create();

        var flow = await harness.Adapter.GetMonthlyFlowAsync(
            new ExternalId(TemenosFixtures.SeededCustomerId), new YearMonth(2024, 1), CancellationToken.None);

        flow.IsSuccess.Should().BeTrue();
        flow.Value.CreditTotal.Should().Be(125_000m);
        flow.Value.DebitTotal.Should().Be(40_000m);

        // INT-22 measures the ceiling against everything that moved. A customer who received and
        // spent the same sum has used the account, and netting it to zero is how a simplified file
        // passes a ceiling it should have breached.
        flow.Value.Total.Should().Be(165_000m);
        flow.Value.Currency.Should().Be(TemenosFixtures.SeededCurrency);
    }

    [Fact]
    public async Task A_page_token_that_does_not_advance_is_refused_rather_than_walked_for_ever()
    {
        using var harness = TemenosTestHarness.Create();

        // An installation that echoes the token it was handed. Unguarded, this is a Hangfire job
        // that spins for ever with nothing in the logs.
        harness.Transport.ForcedTransactionsAnswer = (
            HttpStatusCode.OK,
            """{"header":{"status":"Success","page_token":"stuck"},"body":[]}""");

        var flow = await harness.Adapter.GetMonthlyFlowAsync(
            new ExternalId(TemenosFixtures.SeededCustomerId), new YearMonth(2024, 1), CancellationToken.None);

        flow.IsFailure.Should().BeTrue();
        flow.Code.Should().Be(IntegrationErrors.UnexpectedResponse);
    }

    // ── Configuration failures ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_connection_with_no_stored_credential_makes_no_call_at_all()
    {
        using var harness = TemenosTestHarness.Create(credential: null);

        var result = await harness.Adapter.GetBalanceAsync(
            new ExternalId(TemenosFixtures.SeededAccountId), CancellationToken.None);

        result.Family.Should().Be(ErrorFamily.Technical);
        result.Code.Should().Be(IntegrationErrors.CredentialMissing);

        // An unauthenticated call would come back 401 and read like REFUSED credentials rather
        // than like ABSENT ones, and the two send an administrator to different screens.
        harness.Transport.ApiCalls.Should().Be(0);
        harness.Transport.TokenRequests.Should().Be(0);
    }

    [Fact]
    public async Task A_tenant_with_no_active_Temenos_connection_is_told_so_and_nothing_is_called()
    {
        using var harness = TemenosTestHarness.Create(active: false);

        var result = await harness.Adapter.GetBalanceAsync(
            new ExternalId(TemenosFixtures.SeededAccountId), CancellationToken.None);

        result.Family.Should().Be(ErrorFamily.Technical);
        result.Code.Should().Be(IntegrationErrors.NoActiveConnection);
        harness.Transport.ApiCalls.Should().Be(0);
    }

    // ── What reaches the wire ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Every_call_carries_the_journal_correlation_id_the_company_and_a_bearer_token()
    {
        using var harness = TemenosTestHarness.Create();

        await harness.Adapter.GetBalanceAsync(
            new ExternalId(TemenosFixtures.SeededAccountId), CancellationToken.None);

        var request = harness.Transport.Requests.Should().ContainSingle().Subject;

        // The SAME id the journal recorded. One id in our row and in the installation's own log
        // is what lets a support ticket join two deployments that share no database.
        request.CorrelationId.Should().Be(harness.Journal.Rows[0].Correlation);
        request.CompanyId.Should().Be(TemenosFixtures.CompanyId);
        request.BearerToken.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_write_carries_the_idempotency_key_as_a_header_and_a_read_does_not()
    {
        using var harness = TemenosTestHarness.Create();

        await harness.Adapter.CreateCustomerAsync(Payload("CRM-HEADER"), Key("header"), CancellationToken.None);

        var search = harness.Transport.Requests[0];
        var creation = harness.Transport.Requests[1];

        search.IdempotencyKey.Should().BeNull("a search changes nothing and needs no key");
        creation.IdempotencyKey.Should().Be(Key("header").Value,
            "sent on the chance the installation honours it; the pre-create search is what the "
            + "adapter actually relies on");
    }

    [Fact]
    public async Task A_static_token_connection_sends_the_vault_value_and_asks_no_authorisation_server()
    {
        using var harness = TemenosTestHarness.WithStaticToken("transact-long-lived-token");

        await harness.Adapter.GetBalanceAsync(
            new ExternalId(TemenosFixtures.SeededAccountId), CancellationToken.None);

        harness.Transport.TokenRequests.Should().Be(0);
        harness.Transport.Requests[0].BearerToken.Should().Be("transact-long-lived-token");
    }

    [Fact]
    public async Task The_KYC_grade_goes_to_the_sub_resource_so_a_grade_change_cannot_blank_the_party()
    {
        using var harness = TemenosTestHarness.Create();

        var result = await harness.Adapter.SetKycLevelAsync(
            new ExternalId(TemenosFixtures.SeededCustomerId), KycLevel.Full,
            Key("kyc"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        var request = harness.Transport.Requests.Should().ContainSingle().Subject;

        request.Method.Should().Be(HttpMethod.Put);
        request.Uri.AbsolutePath.Should().EndWith("/kycStatus",
            "a one-field PUT on the party itself would, on a system whose PUT is a replace, erase "
            + "the address and the identity document");
        request.Body.Should().Contain("FULL").And.NotContain("mnemonic");
    }

    // ── INT-08, criterion 1: a row on every path ────────────────────────────────────────────

    [Fact]
    public async Task A_successful_call_writes_one_journal_row_carrying_the_HTTP_status()
    {
        using var harness = TemenosTestHarness.Create();

        await harness.Adapter.GetBalanceAsync(
            new ExternalId(TemenosFixtures.SeededAccountId), CancellationToken.None);

        var row = harness.Journal.Rows.Should().ContainSingle().Subject;

        row.Operation.Should().Be("ReadBalance");
        row.Success.Should().BeTrue();
        row.TenantId.Should().Be(TemenosTestHarness.TenantId);
        row.ConnectionId.Should().Be(TemenosTestHarness.ConnectionId);
        row.HttpStatus.Should().Be(200,
            "the status is the one fact an IntegrationResult cannot carry, and the specification "
            + "names it as a column of this journal");
        row.Endpoint.Should().Contain("balances");
    }

    [Fact]
    public async Task A_failed_call_writes_a_row_too_because_a_gap_reads_as_an_idle_period()
    {
        using var harness = TemenosTestHarness.Create();
        harness.Transport.ForcedApiStatus = HttpStatusCode.ServiceUnavailable;
        harness.Transport.ForcedApiBody = TemenosFixtures.MaintenanceError;

        await harness.Adapter.GetBalanceAsync(
            new ExternalId(TemenosFixtures.SeededAccountId), CancellationToken.None);

        var row = harness.Journal.Rows.Should().ContainSingle().Subject;

        row.Success.Should().BeFalse();
        row.Family.Should().Be(ErrorFamily.Transient);
        row.Code.Should().Be(IntegrationErrors.Unavailable);
        row.HttpStatus.Should().Be(503);
    }

    [Fact]
    public async Task A_call_that_never_left_is_journalled_as_well()
    {
        using var harness = TemenosTestHarness.Create();

        await harness.Adapter.OpenAccountAsync(
            new ExternalId(TemenosFixtures.SeededCustomerId), "PRET-SCOLAIRE",
            Key("journalled-mapping"), CancellationToken.None);

        var row = harness.Journal.Rows.Should().ContainSingle().Subject;

        // The early return on a missing mapping is exactly the call a controller asks about, and
        // an adapter that journalled only what reached the network would leave it as a gap.
        row.Operation.Should().Be("OpenAccount");
        row.Code.Should().Be(IntegrationErrors.MappingMissing);
        row.HttpStatus.Should().BeNull("nothing was sent, so there is no status to report");
    }

    [Fact]
    public async Task One_logical_operation_is_one_row_even_when_it_makes_several_calls()
    {
        using var harness = TemenosTestHarness.Create();

        await harness.Adapter.CreateCustomerAsync(Payload("CRM-ONE-ROW"), Key("one-row"), CancellationToken.None);

        harness.Transport.ApiCalls.Should().Be(2, "a search and a creation");
        harness.Journal.Rows.Should().ContainSingle().Which.Operation.Should().Be("CreateCustomer",
            "INT-08's statistics group on the operation, so splitting an onboarding into two rows "
            + "would report a creation rate twice the real one");
    }

    // ── Health (INT-03) ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_health_check_mints_a_fresh_token_and_reports_a_latency()
    {
        using var harness = TemenosTestHarness.Create();

        var health = await harness.Adapter.CheckHealthAsync(harness.Connection, CancellationToken.None);

        health.IsHealthy.Should().BeTrue();
        health.Latency.Should().NotBeNull();
        harness.Transport.TokenRequests.Should().Be(1,
            "the activation screen is asking whether these credentials work right now, which a "
            + "cached token cannot answer");
    }

    [Fact]
    public async Task A_health_check_against_a_refused_credential_is_unhealthy_and_says_why()
    {
        using var harness = TemenosTestHarness.Create();
        harness.Transport.TokenStatus = HttpStatusCode.Unauthorized;

        var health = await harness.Adapter.CheckHealthAsync(harness.Connection, CancellationToken.None);

        health.IsHealthy.Should().BeFalse();
        health.Detail.Should().Contain(IntegrationErrors.AuthenticationRefused);
    }

    [Fact]
    public async Task A_health_check_on_a_row_carrying_the_wrong_settings_fails_without_a_call()
    {
        using var harness = TemenosTestHarness.Create();

        var foreign = IntegrationConnection.Create(
            tenantId: TemenosTestHarness.TenantId,
            family: IntegrationFamily.CoreBanking,
            kind: IntegrationKind.Fake,
            mode: IntegrationMode.Api,
            name: "Pas du Temenos",
            settings: new FakeSettings(),
            createdBy: Guid.NewGuid(),
            clock: TimeProvider.System);

        var health = await harness.Adapter.CheckHealthAsync(foreign, CancellationToken.None);

        health.IsHealthy.Should().BeFalse();
        health.Detail.Should().Contain("BaseUrl");
        harness.Transport.ApiCalls.Should().Be(0);
    }

    [Fact]
    public async Task The_credential_is_read_from_the_vault_under_the_connection_s_own_key()
    {
        using var harness = TemenosTestHarness.Create();

        await harness.Adapter.GetBalanceAsync(
            new ExternalId(TemenosFixtures.SeededAccountId), CancellationToken.None);

        // Per connection and not per tenant: an IMF has a core banking system and two insurers at
        // once, and a tenant-wide key would make saving the second credential destroy the first.
        await harness.Secrets.Received().GetValueAsync(
            Arg.Is<SecretKey>(k => k.TenantId == TemenosTestHarness.TenantId
                                   && k.EntityId == TemenosTestHarness.ConnectionId
                                   && k.Scope == "integration"),
            Arg.Any<CancellationToken>());
    }

    // ── INT-09, the half that only becomes true with a real adapter in the pipeline ─────────

    [Fact]
    public async Task A_cbs_that_keeps_failing_opens_the_circuit_and_the_state_is_readable()
    {
        using var harness = TemenosTestHarness.Create();
        harness.Transport.ForcedApiStatus = HttpStatusCode.ServiceUnavailable;
        harness.Transport.ForcedApiBody = TemenosFixtures.MaintenanceError;

        // Before anything has failed the provider has no opinion — null is "no circuit yet", not
        // "closed", and the module's health check reports it as unknown.
        harness.Pipelines.GetCircuitState(TemenosTestHarness.ConnectionId).Should().BeNull();

        // Enough failures to pass DefaultFailureThreshold. The read pipeline retries, so each call
        // contributes more than one failure to the breaker; the loop is generous rather than
        // tuned, because the exact count is the pipeline's business and not this test's claim.
        for (var i = 0; i < 12; i++)
        {
            await harness.Adapter.GetBalanceAsync(
                new ExternalId(TemenosFixtures.SeededAccountId), CancellationToken.None);
        }

        harness.Pipelines.GetCircuitState(TemenosTestHarness.ConnectionId)
            .Should().Be(CircuitState.Open,
                "a Transact installation that has stopped answering must stop being called on "
                + "every command, and the module's health check has to be able to say so");
    }

    [Fact]
    public async Task An_open_circuit_is_still_a_result_and_never_an_exception()
    {
        using var harness = TemenosTestHarness.Create();
        harness.Transport.ForcedApiStatus = HttpStatusCode.ServiceUnavailable;
        harness.Transport.ForcedApiBody = TemenosFixtures.MaintenanceError;

        for (var i = 0; i < 12; i++)
        {
            await harness.Adapter.GetBalanceAsync(
                new ExternalId(TemenosFixtures.SeededAccountId), CancellationToken.None);
        }

        // The call after the circuit opened. Polly throws BrokenCircuitException; the module's
        // rule is that a port method never throws, so the transport has to turn it into a
        // Transient result — otherwise opening the breaker would convert a handled outage into
        // an unhandled exception inside a Hangfire job.
        var afterOpen = await harness.Adapter.GetBalanceAsync(
            new ExternalId(TemenosFixtures.SeededAccountId), CancellationToken.None);

        afterOpen.IsFailure.Should().BeTrue();
        afterOpen.Family.Should().Be(ErrorFamily.Transient,
            "an open circuit is a reason to come back later, not a refusal on the merits");
    }
}
