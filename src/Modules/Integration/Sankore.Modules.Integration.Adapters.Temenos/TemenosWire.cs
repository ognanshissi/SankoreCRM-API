namespace Sankore.Modules.Integration.Adapters.Temenos;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

// Every byte this adapter puts on the wire, and every byte it expects back, in one file.
//
// These names come from the PUBLIC Temenos Transact (Party / Holdings) API documentation and
// are the FIRST thing to verify against a real installation. They are hand-written because
// Transact's OpenAPI document is not in this repository and is not redistributable, so there is
// nothing to generate from — the project file says the same thing. Nothing here has ever been
// confirmed against a running Transact.
//
// Why that warning is at the top of the file rather than in a commit message: M02's biometry
// client was hand-written from the same kind of assumption and not ONE field name matched the
// service's own document — it reports `model_versions.service` where the mappers demanded
// `service_version`. Every call therefore mapped to null and every file was reported as
// `BIOMETRY_UNEXPECTED_RESPONSE`, retried three times, against a service that was answering
// perfectly. The failure was invisible precisely because the code was plausible. The same risk
// lives here, and it is contained in exactly two ways:
//
//   - Everything that could be wrong is in THIS file: paths, parameter names, field names,
//     date formats, the error envelope. A verification session against an installation edits one
//     file and nothing else.
//   - A body that does not match what is declared here is reported as
//     `INTEGRATION_UNEXPECTED_RESPONSE` with the operation named, never as a success carrying
//     nulls. A mismatch surfaces as a refused call, not as a customer created with empty fields.
//
// Three assumptions in particular are worth checking before anything else, because each is
// silent when wrong:
//
//   - The enquiry parameter the pre-create search uses
//     (`TemenosQuery.Mnemonic`). If the installation indexes our CRM reference under
//     another name, the search answers "no such customer" every time and INT-12's duplicate
//     protection is gone while every test still passes.
//   - The date format. T24's native date is `yyyyMMdd`; the IRIS JSON layer
//     usually exposes `yyyy-MM-dd`. Requests are sent in the format named by
//     `TemenosWireFormats.RequestDate`; responses are parsed with BOTH accepted, so a
//     wrong guess on the way out is a visible rejection rather than a date read as another day.
//   - Whether the installation honours `Idempotency-Key`
//     (`TemenosHeaders.IdempotencyKey`). It is sent on every write, but the duplicate
//     protection this adapter actually relies on is the pre-create search — see
//     `TemenosCustomerOperations` for why the header is a bonus and the search is the
//     mechanism.
//
/// <summary>
/// Paths of the two APIs, relative to <c>TemenosSettings.BaseUrl</c> and to the API version
/// segment.
///
/// <para>
/// Built as strings rather than composed at each call site so that the version segment and the
/// resource names appear once. Transact deployments differ in their prefix
/// (<c>/irf-provider-container/api/</c> on some installations): that prefix belongs in
/// <c>BaseUrl</c>, which is why nothing here starts with a slash.
/// </para>
/// </summary>
internal static class TemenosPaths
{
    /// <summary>Used when the connection names no API version. Transact's current major line.</summary>
    public const string DefaultApiVersion = "v2.0.0";

    /// <summary>Collection of parties. <c>GET</c> searches it, <c>POST</c> creates one.</summary>
    public static string Customers(string version) => $"{version}/party/customers";

    /// <summary>One party. <c>PUT</c> replaces the record.</summary>
    public static string Customer(string version, string customerId)
        => $"{version}/party/customers/{Uri.EscapeDataString(customerId)}";

    /// <summary>
    /// The party's KYC grade as a SUB-RESOURCE, so setting it cannot blank the rest of the record.
    ///
    /// <para>
    /// A <c>PUT</c> on <see cref="Customer"/> carrying only the KYC field would, on a system whose
    /// <c>PUT</c> is a replace, erase the address and the identity document — which is exactly the
    /// kind of damage nobody notices until a controller asks for the file. If the installation has
    /// no such sub-resource, the fallback is a full update carrying the KYC field, which is why
    /// <see cref="TemenosCustomerRequest"/> is built in one place and already has the field.
    /// </para>
    /// </summary>
    public static string CustomerKyc(string version, string customerId)
        => $"{Customer(version, customerId)}/kycStatus";

    /// <summary>The party's arrangements (accounts).</summary>
    public static string CustomerAccounts(string version, string customerId)
        => $"{version}/holdings/customers/{Uri.EscapeDataString(customerId)}/accounts";

    /// <summary>Collection of accounts. <c>POST</c> opens one.</summary>
    public static string Accounts(string version) => $"{version}/holdings/accounts";

    public static string AccountBalance(string version, string accountId)
        => $"{version}/holdings/accounts/{Uri.EscapeDataString(accountId)}/balances";

    public static string AccountTransactions(string version, string accountId)
        => $"{version}/holdings/accounts/{Uri.EscapeDataString(accountId)}/transactions";
}

/// <summary>Query-string parameter names, in one place for the same reason the paths are.</summary>
internal static class TemenosQuery
{
    /// <summary>
    /// The enquiry field the pre-create search of INT-12 matches our CRM reference against.
    /// Transact's <c>mnemonic</c> is the conventional home of an external short reference.
    /// <b>Verify this first:</b> a wrong name here makes the search silently always empty.
    /// </summary>
    public const string Mnemonic = "mnemonic";

    public const string FromDate = "fromDate";
    public const string ToDate = "toDate";

    /// <summary>IRIS paging. The answer's own <c>page_token</c> is fed back here.</summary>
    public const string PageToken = "page_token";

    public const string PageSize = "page_size";
}

/// <summary>Headers this adapter sets per request. Never on the pooled client's defaults.</summary>
internal static class TemenosHeaders
{
    /// <summary>
    /// The Transact company (branch) the call is made in the name of. A header rather than a
    /// query parameter because it applies to every operation, reads and writes alike.
    /// </summary>
    public const string CompanyId = "companyId";

    /// <summary>
    /// Sent on every write. Transact installations vary on whether they honour it, so this
    /// adapter does not depend on it: see <c>TemenosCustomerOperations</c>.
    /// </summary>
    public const string IdempotencyKey = "Idempotency-Key";
}

/// <summary>Formats, so a parser and a writer cannot drift apart.</summary>
internal static class TemenosWireFormats
{
    /// <summary>What dates are SENT as. See the file comment on why this is worth verifying.</summary>
    public const string RequestDate = "yyyy-MM-dd";

    /// <summary>
    /// What dates are ACCEPTED as, in order. Both, on purpose: T24's native form is
    /// <c>yyyyMMdd</c> and the JSON layer usually rewrites it, but an installation that does not
    /// would otherwise make every value date unreadable.
    /// </summary>
    public static readonly string[] ResponseDates = ["yyyy-MM-dd", "yyyyMMdd", "dd/MM/yyyy"];

    public static string Format(DateOnly date) => date.ToString(RequestDate, CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses one of <see cref="ResponseDates"/>, or <c>null</c>. Invariant culture and an exact
    /// list, never <c>DateOnly.Parse</c>: the process culture decides what <c>02/04/1987</c> means
    /// and the repository has already been bitten by exactly that in the spreadsheet importers.
    /// </summary>
    public static DateOnly? ParseDate(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : DateOnly.TryParseExact(
                value.Trim(), ResponseDates, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed
                : null;

    /// <summary>
    /// Transact reports amounts as JSON strings as often as numbers, and a thousands separator is
    /// not unheard of on an enquiry. Parsed invariantly, returning null rather than zero: an
    /// unreadable amount that became 0 would understate a monthly flow a regulator measures a
    /// ceiling against.
    /// </summary>
    public static decimal? ParseAmount(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var cleaned = value.Trim().Replace(",", string.Empty, StringComparison.Ordinal);

        return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }
}

/// <summary>
/// One <see cref="JsonSerializerOptions"/> for the whole adapter.
///
/// <para>
/// <c>PropertyNamingPolicy</c> is deliberately left null: every member below carries an explicit
/// <see cref="JsonPropertyNameAttribute"/>, because Transact mixes camelCase business fields with
/// snake_case paging fields in the SAME document and no single policy describes both.
/// </para>
/// </summary>
internal static class TemenosJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new TemenosLenientStringConverter() },
    };
}

/// <summary>
/// Reads a JSON scalar of any shape into a <c>string</c>.
///
/// <para>
/// Every scalar of <c>TemenosWire.cs</c> is typed as <c>string?</c> — amounts, dates, balances,
/// indicators — because T24 is a string-typed system and its JSON layer reports a balance as
/// <c>"125000.00"</c> on one installation and as <c>125000.00</c> on the next. Without this
/// converter the second case throws a <c>JsonException</c>, which this adapter correctly reports
/// as <c>INTEGRATION_UNEXPECTED_RESPONSE</c> — correctly, and uselessly: the answer was perfectly
/// good and the whole account list is refused over a pair of quotation marks.
/// </para>
///
/// <para>
/// Numbers are written back out with <see cref="System.Text.Json.JsonElement.GetRawText"/> rather
/// than parsed and re-rendered, so a balance never passes through a <c>double</c> and cannot lose
/// a centime on the way in. Parsing into a <c>decimal</c> is <c>TemenosWireFormats.ParseAmount</c>'s
/// job, invariantly and in one place.
/// </para>
/// </summary>
internal sealed class TemenosLenientStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Null => null,
            JsonTokenType.True => bool.TrueString,
            JsonTokenType.False => bool.FalseString,

            // Raw text, so 125000.00 stays 125000.00 and never becomes 125000 or 1.25E+05.
            JsonTokenType.Number => GetRawNumber(ref reader),

            // An object or an array where a scalar was declared is a genuine contract mismatch,
            // and letting it through as "{...}" would store that string as an account number.
            _ => throw new JsonException(
                $"Expected a scalar for a string field; found {reader.TokenType}. See TemenosWire.cs."),
        };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value);
    }

    private static string GetRawNumber(ref Utf8JsonReader reader)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return document.RootElement.GetRawText();
    }
}

// ── Envelope ────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The IRIS envelope every answer arrives in: a header, a body that is ALWAYS an array even for
/// a single record, and an error block.
///
/// <para>
/// Note that <see cref="Error"/> can be populated on an HTTP 200 — IRIS reports business
/// refusals inside a successful response as well as through status codes. Both paths go through
/// <c>TemenosErrorClassifier</c>, so a refusal cannot be read as a success merely because the
/// transport was happy.
/// </para>
/// </summary>
internal sealed record TemenosEnvelope<T>(
    [property: JsonPropertyName("header")] TemenosResponseHeader? Header,
    [property: JsonPropertyName("body")] List<T>? Body,
    [property: JsonPropertyName("error")] TemenosErrorBlock? Error)
{
    /// <summary>The single record of a single-record answer, or null.</summary>
    public T? First => Body is { Count: > 0 } ? Body[0] : default;

    public bool HasError => Error?.ErrorDetails is { Count: > 0 };
}

/// <summary>
/// Paging and status. <c>page_token</c> is the opaque continuation token: absent or empty means
/// the page just read was the last one.
/// </summary>
internal sealed record TemenosResponseHeader(
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("page_token")] string? PageToken,
    [property: JsonPropertyName("page_size")] int? PageSize,
    [property: JsonPropertyName("total_size")] int? TotalSize);

internal sealed record TemenosErrorBlock(
    [property: JsonPropertyName("errorDetails")] List<TemenosErrorDetail>? ErrorDetails);

/// <summary>
/// One refusal. <see cref="Type"/> is what separates a rejected VALUE (a business refusal the
/// clerk can act on) from a malformed REQUEST (our bug) — see <c>TemenosErrorClassifier</c>.
/// </summary>
internal sealed record TemenosErrorDetail(
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("fieldName")] string? FieldName);

// ── Authentication ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The OAuth 2.0 client-credentials answer (RFC 6749 §5.1), which Transact installations front
/// with a standard authorisation server.
///
/// <para>
/// <see cref="ExpiresIn"/> is optional in the RFC. A token that does not say when it expires is
/// cached for <c>TemenosAdapterOptions.FallbackTokenLifetime</c> and not for ever: see
/// <c>TemenosTokenCache</c>.
/// </para>
/// </summary>
internal sealed record TemenosOAuthToken(
    [property: JsonPropertyName("access_token")] string? AccessToken,
    [property: JsonPropertyName("token_type")] string? TokenType,
    [property: JsonPropertyName("expires_in")] int? ExpiresIn,
    [property: JsonPropertyName("scope")] string? Scope);

// ── Party API ──────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// A party as Transact returns it. Only the fields this module reads are declared: an adapter
/// that declared the whole record would have forty more names to verify and would use none of
/// them.
/// </summary>
internal sealed record TemenosCustomer(
    [property: JsonPropertyName("customerId")] string? CustomerId,
    [property: JsonPropertyName("mnemonic")] string? Mnemonic,
    [property: JsonPropertyName("shortName")] string? ShortName,
    [property: JsonPropertyName("kycStatus")] string? KycStatus);

/// <summary>
/// A party as Transact accepts it.
///
/// <para>
/// Every code field holds an EXTERNAL code: the CRM's own codes are translated through
/// <c>integration_mapping</c> before this record is built, and a missing translation refuses the
/// call rather than passing a CRM code through (INT-04). The one exception is
/// <see cref="Mnemonic"/>, which is our own reference on purpose — it is what the pre-create
/// search of INT-12 finds the customer by.
/// </para>
/// </summary>
internal sealed record TemenosCustomerRequest
{
    [JsonPropertyName("mnemonic")] public string? Mnemonic { get; init; }

    [JsonPropertyName("shortName")] public string? ShortName { get; init; }

    [JsonPropertyName("name1")] public string? Name1 { get; init; }

    [JsonPropertyName("givenNames")] public string? GivenNames { get; init; }

    [JsonPropertyName("familyName")] public string? FamilyName { get; init; }

    [JsonPropertyName("dateOfBirth")] public string? DateOfBirth { get; init; }

    [JsonPropertyName("gender")] public string? Gender { get; init; }

    [JsonPropertyName("maritalStatus")] public string? MaritalStatus { get; init; }

    [JsonPropertyName("nationality")] public string? Nationality { get; init; }

    [JsonPropertyName("residence")] public string? Residence { get; init; }

    [JsonPropertyName("legalDocName")] public string? LegalDocumentType { get; init; }

    [JsonPropertyName("legalId")] public string? LegalDocumentNumber { get; init; }

    [JsonPropertyName("phone1")] public string? Phone { get; init; }

    [JsonPropertyName("email1")] public string? Email { get; init; }

    [JsonPropertyName("street")] public string? Street { get; init; }

    [JsonPropertyName("townCountry")] public string? Town { get; init; }

    [JsonPropertyName("country")] public string? Country { get; init; }

    [JsonPropertyName("occupation")] public string? Profession { get; init; }

    [JsonPropertyName("industry")] public string? Sector { get; init; }

    /// <summary>The Transact company the party is opened in — the mapped agency code.</summary>
    [JsonPropertyName("company")] public string? Company { get; init; }

    [JsonPropertyName("kycStatus")] public string? KycStatus { get; init; }
}

/// <summary>The KYC sub-resource's body. One field, so a grade change cannot touch anything else.</summary>
internal sealed record TemenosKycRequest(
    [property: JsonPropertyName("kycStatus")] string KycStatus);

// ── Holdings API ───────────────────────────────────────────────────────────────────────────────

/// <summary>
/// An arrangement (account) as Transact returns it.
///
/// <para>
/// Three balance fields, and they are not interchangeable: <see cref="OnlineActualBalance"/> is
/// the booked balance, <see cref="AvailableBalance"/> is what may be spent (it nets locked
/// amounts and authorised overdraft), <see cref="WorkingBalance"/> includes same-day uncleared
/// movements. The counter needs both of the first two, which is why <c>CbsBalance</c> has a
/// nullable <c>AvailableBalance</c> beside the balance rather than one number.
/// </para>
/// </summary>
internal sealed record TemenosAccount(
    [property: JsonPropertyName("accountId")] string? AccountId,
    [property: JsonPropertyName("accountReference")] string? AccountReference,
    [property: JsonPropertyName("accountName")] string? AccountName,
    [property: JsonPropertyName("productCode")] string? ProductCode,
    [property: JsonPropertyName("productName")] string? ProductName,
    [property: JsonPropertyName("currency")] string? Currency,
    [property: JsonPropertyName("onlineActualBalance")] string? OnlineActualBalance,
    [property: JsonPropertyName("availableBalance")] string? AvailableBalance,
    [property: JsonPropertyName("workingBalance")] string? WorkingBalance,
    [property: JsonPropertyName("accountStatus")] string? AccountStatus,
    [property: JsonPropertyName("openingDate")] string? OpeningDate);

/// <summary>Body of an account opening. The product code here is the EXTERNAL one (INT-13).</summary>
internal sealed record TemenosAccountRequest
{
    [JsonPropertyName("customerId")] public string? CustomerId { get; init; }

    [JsonPropertyName("productCode")] public string? ProductCode { get; init; }

    [JsonPropertyName("currency")] public string? Currency { get; init; }

    [JsonPropertyName("company")] public string? Company { get; init; }
}

/// <summary>
/// One movement.
///
/// <para>
/// <see cref="DebitCreditIndicator"/> is read first and the sign of <see cref="Amount"/> is the
/// fallback, because installations differ on whether a debit is reported as a negative number or
/// as a positive number with an indicator. Reading only the sign on a system that uses the
/// indicator would file every debit as a credit — and a monthly flow built from that is wrong in
/// the one direction that matters to a KYC ceiling.
/// </para>
/// </summary>
internal sealed record TemenosTransaction(
    [property: JsonPropertyName("transactionReference")] string? TransactionReference,
    [property: JsonPropertyName("valueDate")] string? ValueDate,
    [property: JsonPropertyName("bookingDate")] string? BookingDate,
    [property: JsonPropertyName("amount")] string? Amount,
    [property: JsonPropertyName("currency")] string? Currency,
    [property: JsonPropertyName("debitCreditIndicator")] string? DebitCreditIndicator,
    [property: JsonPropertyName("narrative")] string? Narrative,
    [property: JsonPropertyName("counterpartyName")] string? CounterpartyName);

/// <summary>
/// The two values of <see cref="TemenosTransaction.DebitCreditIndicator"/>, and the two strings
/// <c>CbsTransaction.Direction</c> carries. Kept together so the translation reads as a table.
/// </summary>
internal static class TemenosDirections
{
    public const string WireDebit = "DEBIT";
    public const string WireCredit = "CREDIT";

    /// <summary>What <c>CbsTransaction.Direction</c> holds — the same two words the Fake uses.</summary>
    public const string Debit = "Debit";
    public const string Credit = "Credit";
}

/// <summary>
/// KYC grades, both ways.
///
/// <para>
/// Transact has no standard vocabulary for a KYC tier, so these are the installation's values and
/// belong with the rest of what must be verified. They are NOT routed through
/// <c>integration_mapping</c>: <c>MappingDomain</c> has no KYC domain, and inventing one would be
/// a schema decision outside INT-12.
/// </para>
/// </summary>
internal static class TemenosKycStatuses
{
    public const string None = "NOT_STARTED";
    public const string Simplified = "SIMPLIFIED";
    public const string Full = "FULL";
}
