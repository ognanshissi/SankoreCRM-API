namespace Sankore.Modules.Integration.Tests.Adapters.Temenos;

/// <summary>
/// Recorded Transact bodies, as <c>const string</c> and not as files.
///
/// <para>
/// In the source on purpose: these payloads ARE the assumption the adapter is built on (see the
/// banner at the top of <c>TemenosWire.cs</c>), so they belong where a reviewer comparing the
/// adapter with an installation's own document will read them. A file under a test-data folder
/// would be one indirection away from the mappers it is supposed to be checked against, and
/// copying the shipped file at build time is a build concern nobody would maintain.
/// </para>
///
/// <para>
/// Three deliberate inconsistencies are baked in, because a real Transact has them and a tidy
/// double would hide them:
/// </para>
///
/// <list type="bullet">
/// <item><c>onlineActualBalance</c> is a JSON NUMBER and <c>availableBalance</c> a JSON STRING.
///   Installations differ, and the adapter's lenient string converter is what makes both
///   readable — a fixture that used one form everywhere would let the other regress silently.</item>
/// <item>Dates come back as <c>yyyy-MM-dd</c> here and as <c>yyyyMMdd</c> in the transaction
///   rows, which is T24's native form.</item>
/// <item>The error envelope is nested under <c>error.errorDetails</c> and carries a
///   <c>type</c> — which is what separates a refused VALUE from a malformed REQUEST.</item>
/// </list>
/// </summary>
internal static class TemenosFixtures
{
    // ── Coordinates ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// An RFC 1918 host on purpose. A Transact installation is frequently on-premise, and this
    /// base URL is what a run would prove unreachable if <c>SsrfSafeHandler</c> were ever wired
    /// onto the adapter's HttpClient.
    /// </summary>
    public const string BaseUrl = "https://10.20.30.40/irf-provider-container/api";

    public const string TokenEndpoint = "https://10.20.30.40/oauth2/token";

    public const string ApiVersion = "v2.0.0";

    public const string CompanyId = "CI0010001";

    public const string ClientId = "sankore-crm";

    /// <summary>The value the vault hands back. Never a real secret, obviously.</summary>
    public const string ClientSecret = "dev-only-client-secret";

    // ── Seeded portfolio ───────────────────────────────────────────────────────────────────

    public const string SeededCustomerId = "100001";

    /// <summary>The CRM reference the seeded party is registered under in Transact.</summary>
    public const string SeededMnemonic = "CRM-SEED-0001";

    public const string SeededAccountId = "ACC-100001-1";

    public const string SeededCurrency = "XOF";

    /// <summary>A CRM product code the seeded mapping table translates.</summary>
    public const string SeededCrmProductCode = "EPARGNE-TONTINE";

    /// <summary>What Transact calls it, on the other side of <c>integration_mapping</c>.</summary>
    public const string SeededExternalProductCode = "6001";

    // ── Authentication ─────────────────────────────────────────────────────────────────────

    /// <summary>RFC 6749 §5.1. <c>{0}</c> is the token, <c>{1}</c> the lifetime in seconds.</summary>
    public const string TokenResponse =
        """
        {
          "access_token": "{0}",
          "token_type": "Bearer",
          "expires_in": {1},
          "scope": "transact.party transact.holdings"
        }
        """;

    /// <summary>An authorisation server that answered 200 with no token — a real failure mode.</summary>
    public const string TokenResponseWithoutToken =
        """
        {
          "token_type": "Bearer",
          "expires_in": 3600
        }
        """;

    // ── Party API ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// An enquiry that matched nothing. A 200 with an empty <c>body</c>, which is what makes the
    /// pre-create search's "no match" path the ordinary one rather than an error path.
    /// </summary>
    public const string CustomerSearchEmpty =
        """
        {
          "header": { "status": "Success", "total_size": 0 },
          "body": []
        }
        """;

    /// <summary><c>{0}</c> customerId, <c>{1}</c> mnemonic.</summary>
    public const string CustomerSearchOneHit =
        """
        {
          "header": { "status": "Success", "total_size": 1 },
          "body": [
            { "customerId": "{0}", "mnemonic": "{1}", "shortName": "OUATTARA AWA", "kycStatus": "SIMPLIFIED" }
          ]
        }
        """;

    /// <summary>
    /// Two parties under one CRM reference. The state INT-12's search must refuse to add a third
    /// to. <c>{0}</c> and <c>{1}</c> are the two customer ids.
    /// </summary>
    public const string CustomerSearchTwoHits =
        """
        {
          "header": { "status": "Success", "total_size": 2 },
          "body": [
            { "customerId": "{0}", "mnemonic": "DOUBLE", "shortName": "OUATTARA AWA" },
            { "customerId": "{1}", "mnemonic": "DOUBLE", "shortName": "OUATTARA A." }
          ]
        }
        """;

    /// <summary><c>{0}</c> the minted customerId, <c>{1}</c> the mnemonic we sent.</summary>
    public const string CustomerCreated =
        """
        {
          "header": { "status": "Success" },
          "body": [
            { "customerId": "{0}", "mnemonic": "{1}", "shortName": "OUATTARA AWA", "kycStatus": "SIMPLIFIED" }
          ]
        }
        """;

    /// <summary>
    /// A creation that succeeded without naming the party. Unusable — the id is what
    /// <c>integration_reference</c> stores — and therefore refused rather than stored empty.
    /// </summary>
    public const string CustomerCreatedWithoutId =
        """
        {
          "header": { "status": "Success" },
          "body": [ { "mnemonic": "CRM-000123", "shortName": "OUATTARA AWA" } ]
        }
        """;

    /// <summary>What a <c>PUT</c> acknowledges. <c>{0}</c> the customerId.</summary>
    public const string CustomerUpdated =
        """
        {
          "header": { "status": "Success" },
          "body": [ { "customerId": "{0}", "kycStatus": "FULL" } ]
        }
        """;

    // ── Holdings API ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One arrangement. <c>{0}</c> accountId, <c>{1}</c> external product code, <c>{2}</c>
    /// currency, <c>{3}</c> balance (a NUMBER), <c>{4}</c> available balance (a STRING).
    /// </summary>
    public const string AccountItem =
        """
        {
              "accountId": "{0}",
              "accountReference": "CI00{0}",
              "accountName": "Compte epargne",
              "productCode": "{1}",
              "productName": "Epargne tontine",
              "currency": "{2}",
              "onlineActualBalance": {3},
              "availableBalance": "{4}",
              "workingBalance": "{4}",
              "accountStatus": "ACTIVE",
              "openingDate": "2022-06-01"
            }
        """;

    /// <summary><c>{0}</c> the joined <see cref="AccountItem"/> values.</summary>
    public const string AccountsPage =
        """
        {
          "header": { "status": "Success" },
          "body": [ {0} ]
        }
        """;

    /// <summary>
    /// One movement. <c>{0}</c> reference, <c>{1}</c> value date in T24's native
    /// <c>yyyyMMdd</c>, <c>{2}</c> amount, <c>{3}</c> DEBIT or CREDIT, <c>{4}</c> narrative.
    /// </summary>
    public const string TransactionItem =
        """
        {
              "transactionReference": "{0}",
              "valueDate": "{1}",
              "bookingDate": "{1}",
              "amount": "{2}",
              "currency": "XOF",
              "debitCreditIndicator": "{3}",
              "narrative": "{4}",
              "counterpartyName": null
            }
        """;

    /// <summary>
    /// A page of movements. <c>{0}</c> the joined items, <c>{1}</c> the <c>page_token</c> JSON
    /// value — a quoted token, or a bare <c>null</c> on the last page.
    /// </summary>
    public const string TransactionsPage =
        """
        {
          "header": { "status": "Success", "page_token": {1}, "page_size": 2 },
          "body": [ {0} ]
        }
        """;

    // ── Refusals ───────────────────────────────────────────────────────────────────────────

    /// <summary>The far end already holds the record. Functional, never retried.</summary>
    public const string DuplicateError =
        """
        {
          "header": { "status": "Failure" },
          "error": {
            "errorDetails": [
              { "code": "T24.CUSTOMER.0012", "message": "CUSTOMER ALREADY EXISTS", "type": "BUSINESS" }
            ]
          }
        }
        """;

    /// <summary>
    /// A refused VALUE: the envelope names the field. Functional — a clerk can correct the CRM
    /// record and the command can be replayed.
    /// </summary>
    public const string RejectedFieldError =
        """
        {
          "header": { "status": "Failure" },
          "error": {
            "errorDetails": [
              {
                "code": "T24.PARTY.0045",
                "message": "VALUE NOT ON LIST",
                "type": "BUSINESS",
                "fieldName": "nationality"
              }
            ]
          }
        }
        """;

    /// <summary>
    /// A malformed REQUEST: no field named, and the type says it is not a business matter.
    /// Technical — ours to fix, and a clerk can do nothing with it.
    /// </summary>
    public const string MalformedRequestError =
        """
        {
          "header": { "status": "Failure" },
          "error": {
            "errorDetails": [
              { "code": "IRIS.REQUEST.0001", "message": "UNABLE TO PARSE REQUEST", "type": "TECHNICAL" }
            ]
          }
        }
        """;

    /// <summary>A product the installation does not know (INT-13, criterion 2's far-end half).</summary>
    public const string UnknownProductError =
        """
        {
          "header": { "status": "Failure" },
          "error": {
            "errorDetails": [
              { "code": "T24.ACCOUNT.0099", "message": "INVALID PRODUCT CODE", "type": "BUSINESS" }
            ]
          }
        }
        """;

    /// <summary>A record T24 does not hold, answered on a 400 rather than a 404 — it does that.</summary>
    public const string RecordNotFoundError =
        """
        {
          "header": { "status": "Failure" },
          "error": {
            "errorDetails": [
              { "code": "T24.ENQUIRY.0004", "message": "RECORD NOT FOUND", "type": "BUSINESS" }
            ]
          }
        }
        """;

    /// <summary>A maintenance window. Transient, and the message must not win over the status.</summary>
    public const string MaintenanceError =
        """
        {
          "header": { "status": "Failure" },
          "error": {
            "errorDetails": [
              { "code": "T24.SYSTEM.0001", "message": "COB IN PROGRESS, RECORD NOT FOUND", "type": "TECHNICAL" }
            ]
          }
        }
        """;

    /// <summary>
    /// A refusal carried on an HTTP 200, which IRIS does. The transport must classify it rather
    /// than read the envelope as a success because the transport layer was happy.
    /// </summary>
    public const string ErrorOnHttp200 = DuplicateError;

    /// <summary>What a gateway in front of a dead installation answers instead of JSON.</summary>
    public const string GatewayHtml =
        "<html><head><title>502 Bad Gateway</title></head><body>nginx</body></html>";
}
