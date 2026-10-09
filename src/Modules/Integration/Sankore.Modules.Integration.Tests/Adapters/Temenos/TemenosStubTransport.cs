namespace Sankore.Modules.Integration.Tests.Adapters.Temenos;

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

/// <summary>
/// A Temenos Transact installation, in a <see cref="HttpMessageHandler"/>.
///
/// <para>
/// It exists because INT-12's and INT-13's last criteria ("contract tests green against the
/// sandbox") cannot be met: there is no sandbox and no credentials. What CAN be held to the
/// contract is the adapter's own behaviour — the pre-create search, the token renewal, the error
/// classification, the pager — and holding it there needs something at the other end of the
/// socket. This is that something, and it is deliberately a transport double rather than a
/// mocked adapter: everything between <c>TemenosAdapter</c> and the wire, the JSON included, is
/// then genuinely exercised.
/// </para>
///
/// <para>
/// <b>It is not a specification.</b> Its field names and paths are copied from the same public
/// documentation as <c>TemenosWire.cs</c>, so it cannot contradict a mistake in it — the two
/// agree because they were written together, not because either was verified. That is the whole
/// reason <c>TemenosSandboxIntegrationTests</c> exists.
/// </para>
///
/// <para>
/// <b>Stateful, and it must be.</b> A party created through it is findable afterwards, which is
/// what makes the idempotency contract meaningful: the second creation under the same CRM
/// reference is answered from the search and never reaches a <c>POST</c>, and
/// <see cref="CustomerPosts"/> proves it. A stateless double returning a canned id would make that
/// contract pass while the adapter created duplicates.
/// </para>
///
/// <para>
/// Not thread-safe, like any test double, except for the counters — the single-flight test fires
/// fifty concurrent calls at it, so the counters are interlocked and nothing else is touched on
/// that path.
/// </para>
/// </summary>
internal sealed class TemenosStubTransport : HttpMessageHandler
{
    private const string JsonMediaType = "application/json";

    private readonly Dictionary<string, string> _customersByMnemonic = new(StringComparer.Ordinal);
    private readonly HashSet<string> _customers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<AccountRow>> _accountsByCustomer = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<MovementRow>> _movementsByAccount = new(StringComparer.Ordinal);

    /// <summary>
    /// Bodies already answered for an <c>Idempotency-Key</c>.
    ///
    /// <para>
    /// This is the half of the idempotency contract that is the INSTALLATION's, not the adapter's:
    /// it encodes the assumption that Transact honours the header, which is one of the three
    /// things the banner in <c>TemenosWire.cs</c> says to verify first. The adapter does not
    /// depend on it for customers — the pre-create search is what protects those — but an account
    /// opening has no business key to search by, so the header is all there is.
    /// </para>
    /// </summary>
    private readonly Dictionary<string, string> _idempotent = new(StringComparer.Ordinal);

    private int _mintedCustomers;
    private int _mintedAccounts;
    private int _tokenRequests;
    private int _customerPosts;
    private int _accountPosts;
    private int _apiCalls;

    public TemenosStubTransport()
    {
        // The seeded portfolio: one party, one account, five movements in January 2024. Fixed
        // values, because a test that asserts on a figure must keep passing on the next run.
        _customers.Add(TemenosFixtures.SeededCustomerId);
        _customersByMnemonic[TemenosFixtures.SeededMnemonic] = TemenosFixtures.SeededCustomerId;

        _accountsByCustomer[TemenosFixtures.SeededCustomerId] =
        [
            new AccountRow(
                TemenosFixtures.SeededAccountId,
                TemenosFixtures.SeededExternalProductCode,
                TemenosFixtures.SeededCurrency,
                Balance: 125_000m,
                Available: 120_000m),
        ];

        _movementsByAccount[TemenosFixtures.SeededAccountId] =
        [
            new MovementRow("TRX-0001", new DateOnly(2024, 1, 5), 50_000m, Debit: false, "Depot especes"),
            new MovementRow("TRX-0002", new DateOnly(2024, 1, 8), 12_500m, Debit: true, "Retrait GAB"),
            new MovementRow("TRX-0003", new DateOnly(2024, 1, 12), 75_000m, Debit: false, "Virement salaire"),
            new MovementRow("TRX-0004", new DateOnly(2024, 1, 19), 7_500m, Debit: true, "Prime assurance"),
            new MovementRow("TRX-0005", new DateOnly(2024, 1, 26), 20_000m, Debit: true, "Transfert mobile"),
        ];
    }

    // ── Knobs ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// How the installation behaves. A mode rather than a set of booleans because a real
    /// installation is in one state at a time, and the contract suite wants one line to pick it.
    /// </summary>
    public StubMode Mode { get; set; } = StubMode.Healthy;

    /// <summary>
    /// Pages served at a time, whatever <c>page_size</c> the adapter asks for.
    ///
    /// <para>
    /// Two, so the DEFAULT double pages. A double that fits everything in one page is a double
    /// whose pager is never exercised, and the pager breaks the first time a real installation
    /// answers in pages — which is the first time a customer has a busy month.
    /// </para>
    /// </summary>
    public int PageSize { get; set; } = 2;

    /// <summary>Seconds of life the token endpoint reports. Null omits <c>expires_in</c>.</summary>
    public int? TokenLifetimeSeconds { get; set; } = 3600;

    /// <summary>Status the token endpoint answers, when it is to fail.</summary>
    public HttpStatusCode? TokenStatus { get; set; }

    /// <summary>Body the token endpoint answers on a success. Null uses the standard one.</summary>
    public string? TokenBody { get; set; }

    /// <summary>
    /// How many business calls answer 401 before the installation starts accepting the token.
    /// One is the renewal path of INT-12's criterion 2; two proves a second 401 is believed.
    /// </summary>
    public int UnauthorizedApiCalls { get; set; }

    /// <summary>Status every business call answers, overriding the routing below.</summary>
    public HttpStatusCode? ForcedApiStatus { get; set; }

    /// <summary>Body that goes with <see cref="ForcedApiStatus"/>.</summary>
    public string? ForcedApiBody { get; set; }

    /// <summary>Overrides the answer of the pre-create search alone.</summary>
    public (HttpStatusCode Status, string Body)? ForcedSearchAnswer { get; set; }

    /// <summary>Overrides the answer of a customer creation alone.</summary>
    public (HttpStatusCode Status, string Body)? ForcedCreateAnswer { get; set; }

    /// <summary>
    /// Overrides the answer of a transaction page alone, leaving the account list working.
    ///
    /// <para>
    /// Per route and not global, because the faults worth modelling are per route: the monthly
    /// flow reads the accounts and THEN the history, and a global override would make it fail on
    /// the first call and never reach the pager under test.
    /// </para>
    /// </summary>
    public (HttpStatusCode Status, string Body)? ForcedTransactionsAnswer { get; set; }

    // ── Observations ────────────────────────────────────────────────────────────────────────

    public int TokenRequests => Volatile.Read(ref _tokenRequests);

    /// <summary>
    /// Creations that actually reached the installation. The number INT-12's criterion 3 is
    /// about: two creations of one customer must leave this at 1.
    /// </summary>
    public int CustomerPosts => Volatile.Read(ref _customerPosts);

    public int AccountPosts => Volatile.Read(ref _accountPosts);

    /// <summary>Business calls, token requests excluded. Zero proves nothing left the process.</summary>
    public int ApiCalls => Volatile.Read(ref _apiCalls);

    /// <summary>
    /// Every business request, in order, with the headers that matter. Kept so a test can assert
    /// that the correlation id and the idempotency key reached the wire rather than trusting that
    /// the code that sets them is reached.
    /// </summary>
    public List<RecordedRequest> Requests { get; } = [];

    // ── Routing ─────────────────────────────────────────────────────────────────────────────

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var uri = request.RequestUri!;

        if (uri.AbsoluteUri.StartsWith(TemenosFixtures.TokenEndpoint, StringComparison.Ordinal))
            return TokenAnswer();

        Interlocked.Increment(ref _apiCalls);

        // Read ONCE and passed down, rather than read again inside each route: the adapter sends
        // a JsonContent, and a double that consumed it twice would be testing HttpContent's
        // buffering rather than the adapter.
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);

        Requests.Add(new RecordedRequest(
            request.Method,
            uri,
            Header(request, "X-Correlation-Id"),
            Header(request, "Idempotency-Key"),
            Header(request, "companyId"),
            request.Headers.Authorization?.Parameter,
            body));

        // Modelled as a thrown TaskCanceledException and not as a 504, because that is what
        // HttpClient actually does on a dead socket — and it is the branch where the adapter has
        // to tell its own expiry from the caller's cancellation.
        if (Mode == StubMode.TimingOut)
            throw new TaskCanceledException("The stub installation never answered.");

        if (Mode == StubMode.Unreachable)
            throw new HttpRequestException("No route to the stub installation.");

        if (UnauthorizedApiCalls > 0)
        {
            UnauthorizedApiCalls--;
            return Answer(HttpStatusCode.Unauthorized, """{"error":{"errorDetails":[{"code":"AUTH.0001"}]}}""");
        }

        if (ForcedApiStatus is { } forced)
            return Answer(forced, ForcedApiBody ?? string.Empty);

        var path = uri.AbsolutePath;
        var query = ParseQuery(uri.Query);

        // Longest-first, so /{id}/kycStatus is never matched by the /{id} rule — which would
        // write a KYC grade onto the party record and silently blank everything else.
        if (request.Method == HttpMethod.Get && path.EndsWith("/party/customers", StringComparison.Ordinal))
            return SearchCustomers(Param(query, "mnemonic"));

        if (request.Method == HttpMethod.Post && path.EndsWith("/party/customers", StringComparison.Ordinal))
            return CreateCustomer(body);

        if (request.Method == HttpMethod.Put && path.Contains("/party/customers/", StringComparison.Ordinal))
            return UpdateCustomer(path);

        if (request.Method == HttpMethod.Get && path.Contains("/holdings/customers/", StringComparison.Ordinal))
            return ListAccounts(path);

        if (request.Method == HttpMethod.Post && path.EndsWith("/holdings/accounts", StringComparison.Ordinal))
            return OpenAccount(Header(request, "Idempotency-Key"), body);

        if (request.Method == HttpMethod.Get && path.EndsWith("/balances", StringComparison.Ordinal))
            return Balance(path);

        if (request.Method == HttpMethod.Get && path.EndsWith("/transactions", StringComparison.Ordinal))
            return Transactions(path, Param(query, "page_token"), Param(query, "fromDate"), Param(query, "toDate"));

        return Answer(HttpStatusCode.NotFound, TemenosFixtures.RecordNotFoundError);
    }

    // ── Authentication ──────────────────────────────────────────────────────────────────────

    private HttpResponseMessage TokenAnswer()
    {
        var issued = Interlocked.Increment(ref _tokenRequests);

        if (TokenStatus is { } status)
            return Answer(status, """{"error":"invalid_client"}""");

        if (TokenBody is not null)
            return Answer(HttpStatusCode.OK, TokenBody);

        // A DIFFERENT token every time, so a test can tell a renewal from a cache hit by looking
        // at what reached the wire rather than only at a counter.
        var body = Fill(
            TemenosFixtures.TokenResponse,
            $"tok-{issued}",
            (TokenLifetimeSeconds ?? 3600).ToString(CultureInfo.InvariantCulture));

        return Answer(HttpStatusCode.OK, body);
    }

    // ── Party ───────────────────────────────────────────────────────────────────────────────

    private HttpResponseMessage SearchCustomers(string? mnemonic)
    {
        if (ForcedSearchAnswer is { } forced)
            return Answer(forced.Status, forced.Body);

        // The health check's bounded enquiry: no mnemonic, so nothing to match.
        if (string.IsNullOrWhiteSpace(mnemonic))
            return Answer(HttpStatusCode.OK, TemenosFixtures.CustomerSearchEmpty);

        return _customersByMnemonic.TryGetValue(mnemonic, out var customerId)
            ? Answer(HttpStatusCode.OK, Fill(TemenosFixtures.CustomerSearchOneHit, customerId, mnemonic))
            : Answer(HttpStatusCode.OK, TemenosFixtures.CustomerSearchEmpty);
    }

    private HttpResponseMessage CreateCustomer(string? body)
    {
        Interlocked.Increment(ref _customerPosts);

        if (ForcedCreateAnswer is { } forced)
            return Answer(forced.Status, forced.Body);

        if (Mode == StubMode.Duplicating)
            return Answer(HttpStatusCode.Conflict, TemenosFixtures.DuplicateError);

        var mnemonic = Field(body, "mnemonic") ?? $"anonymous-{_mintedCustomers}";

        var customerId = $"2000{++_mintedCustomers:D2}";

        _customers.Add(customerId);
        _customersByMnemonic[mnemonic] = customerId;
        _accountsByCustomer[customerId] = [];

        return Answer(HttpStatusCode.OK, Fill(TemenosFixtures.CustomerCreated, customerId, mnemonic));
    }

    private HttpResponseMessage UpdateCustomer(string path)
    {
        // ".../customers/{id}" or ".../customers/{id}/kycStatus" — the id is the segment right
        // after "customers".
        var customerId = SegmentAfter(path, "customers");

        return customerId is not null && _customers.Contains(customerId)
            ? Answer(HttpStatusCode.OK, Fill(TemenosFixtures.CustomerUpdated, customerId))
            : Answer(HttpStatusCode.NotFound, TemenosFixtures.RecordNotFoundError);
    }

    // ── Holdings ────────────────────────────────────────────────────────────────────────────

    private HttpResponseMessage ListAccounts(string path)
    {
        var customerId = SegmentAfter(path, "customers");

        if (customerId is null || !_customers.Contains(customerId))
            return Answer(HttpStatusCode.NotFound, TemenosFixtures.RecordNotFoundError);

        var rows = _accountsByCustomer.TryGetValue(customerId, out var list) ? list : [];

        return Answer(HttpStatusCode.OK, Fill(TemenosFixtures.AccountsPage, Join(rows.Select(Render))));
    }

    private HttpResponseMessage OpenAccount(string? key, string? body)
    {
        Interlocked.Increment(ref _accountPosts);

        // The installation's own idempotency, assumed and not verified — see the field comment on
        // _idempotent. An account opening has no business key to search by, so a replay under the
        // same key is answered from here and no second arrangement is opened.
        if (key is not null && _idempotent.TryGetValue(key, out var already))
            return Answer(HttpStatusCode.OK, already);

        if (Mode == StubMode.Duplicating)
            return Answer(HttpStatusCode.Conflict, TemenosFixtures.DuplicateError);

        var customerId = Field(body, "customerId");
        var product = Field(body, "productCode");

        if (customerId is null || !_customers.Contains(customerId))
            return Answer(HttpStatusCode.NotFound, TemenosFixtures.RecordNotFoundError);

        if (product is null || !string.Equals(product, TemenosFixtures.SeededExternalProductCode, StringComparison.Ordinal))
            return Answer(HttpStatusCode.BadRequest, TemenosFixtures.UnknownProductError);

        var accountId = $"ACC-{customerId}-{++_mintedAccounts}";

        var row = new AccountRow(accountId, product, TemenosFixtures.SeededCurrency, 0m, 0m);

        _accountsByCustomer[customerId].Add(row);
        _movementsByAccount[accountId] = [];

        var answer = Fill(TemenosFixtures.AccountsPage, Render(row));

        if (key is not null) _idempotent[key] = answer;

        return Answer(HttpStatusCode.OK, answer);
    }

    private HttpResponseMessage Balance(string path)
    {
        var accountId = SegmentAfter(path, "accounts");

        var row = _accountsByCustomer.Values
            .SelectMany(a => a)
            .FirstOrDefault(a => string.Equals(a.AccountId, accountId, StringComparison.Ordinal));

        return row is null
            ? Answer(HttpStatusCode.NotFound, TemenosFixtures.RecordNotFoundError)
            : Answer(HttpStatusCode.OK, Fill(TemenosFixtures.AccountsPage, Render(row)));
    }

    private HttpResponseMessage Transactions(string path, string? pageToken, string? from, string? to)
    {
        if (ForcedTransactionsAnswer is { } forced)
            return Answer(forced.Status, forced.Body);

        var accountId = SegmentAfter(path, "accounts");

        if (accountId is null || !_movementsByAccount.TryGetValue(accountId, out var movements))
            return Answer(HttpStatusCode.NotFound, TemenosFixtures.RecordNotFoundError);

        var fromDate = DateOnly.ParseExact(from!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var toDate = DateOnly.ParseExact(to!, "yyyy-MM-dd", CultureInfo.InvariantCulture);

        var window = movements
            .Where(m => m.ValueDate >= fromDate && m.ValueDate <= toDate)
            .ToList();

        var offset = pageToken is null
            ? 0
            : int.Parse(pageToken, CultureInfo.InvariantCulture);

        var page = window.Skip(offset).Take(PageSize).ToList();
        var next = offset + page.Count;

        // Null and not "" on the last page: an empty token fed back as a cursor would restart the
        // walk from the top, which is the pager bug the contract suite bounds itself against.
        var token = next < window.Count
            ? $"\"{next.ToString(CultureInfo.InvariantCulture)}\""
            : "null";

        return Answer(
            HttpStatusCode.OK,
            Fill(TemenosFixtures.TransactionsPage, Join(page.Select(Render)), token));
    }

    // ── Rendering ───────────────────────────────────────────────────────────────────────────

    private static string Render(AccountRow row) => Fill(
        TemenosFixtures.AccountItem,
        row.AccountId,
        row.ProductCode,
        row.Currency,
        row.Balance.ToString(CultureInfo.InvariantCulture),
        row.Available.ToString(CultureInfo.InvariantCulture));

    private static string Render(MovementRow row) => Fill(
        TemenosFixtures.TransactionItem,
        row.Reference,
        row.ValueDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
        row.Amount.ToString(CultureInfo.InvariantCulture),
        row.Debit ? "DEBIT" : "CREDIT",
        row.Narrative);

    private static string Join(IEnumerable<string> items) => string.Join(",\n    ", items);

    /// <summary>
    /// Fills <c>{0}</c>, <c>{1}</c>… by ordered replacement and NOT with
    /// <see cref="string.Format(string,object?[])"/>, which would throw on the literal braces of
    /// every JSON object in the fixtures.
    /// </summary>
    private static string Fill(string template, params string[] values)
    {
        var filled = template;

        for (var i = 0; i < values.Length; i++)
            filled = filled.Replace($"{{{i}}}", values[i], StringComparison.Ordinal);

        return filled;
    }

    private static HttpResponseMessage Answer(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, JsonMediaType),
    };

    private static string? Header(HttpRequestMessage request, string name)
        => request.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    /// <summary>The path segment following <paramref name="marker"/>, or null.</summary>
    private static string? SegmentAfter(string path, string marker)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var index = Array.IndexOf(segments, marker);

        return index >= 0 && index + 1 < segments.Length
            ? Uri.UnescapeDataString(segments[index + 1])
            : null;
    }

    /// <summary>
    /// The query string as a dictionary. Hand-rolled rather than
    /// <c>System.Web.HttpUtility.ParseQueryString</c>, which lives in an assembly this test
    /// project does not reference.
    /// </summary>
    private static Dictionary<string, string> ParseQuery(string query)
    {
        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0) continue;

            parsed[Uri.UnescapeDataString(pair[..separator])] =
                Uri.UnescapeDataString(pair[(separator + 1)..]);
        }

        return parsed;
    }

    private static string? Param(Dictionary<string, string> query, string name)
        => query.TryGetValue(name, out var value) ? value : null;

    /// <summary>One top-level string field of a request body.</summary>
    private static string? Field(string? json, string name)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        using var document = JsonDocument.Parse(json);

        return document.RootElement.TryGetProperty(name, out var element)
               && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
    }

    private sealed record AccountRow(
        string AccountId, string ProductCode, string Currency, decimal Balance, decimal Available);

    private sealed record MovementRow(
        string Reference, DateOnly ValueDate, decimal Amount, bool Debit, string Narrative);
}

/// <summary>
/// The states the stub installation can be in. One at a time, like a real one.
/// </summary>
internal enum StubMode
{
    /// <summary>Answers the seeded portfolio and accepts writes.</summary>
    Healthy,

    /// <summary>Never answers a business call. The token endpoint still works, so the timeout
    /// under test is the one on the operation and not on authentication.</summary>
    TimingOut,

    /// <summary>The socket never opens — DNS, TLS, a refused connection.</summary>
    Unreachable,

    /// <summary>Refuses every write as already held, while reads keep answering.</summary>
    Duplicating,
}

/// <summary>What the stub saw on the wire. Asserted on, so it carries only what is asserted.</summary>
internal sealed record RecordedRequest(
    HttpMethod Method,
    Uri Uri,
    string? CorrelationId,
    string? IdempotencyKey,
    string? CompanyId,
    string? BearerToken,
    string? Body);
