namespace Sankore.Modules.Integration.Tests.Infrastructure.CallLog;

using System.Globalization;
using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure.CallLog;
using Sankore.Modules.Integration.PublicApi;
using Xunit;

/// <summary>
/// INT-08 criterion 2: <b>no payload and no personal data in clear</b>, in the journal or in the
/// application logs.
///
/// <para>
/// This is the suite that has to be adversarial rather than illustrative. The journal is the one
/// table of this module designed to be handed to a BCEAO or CIMA controller, and "we were careful"
/// is not a property anyone can verify after the fact. So each test below plays the part of an
/// adapter author doing something reasonable that would leak: passing the full URL it actually
/// called, passing the response body as the error code, returning a provider's HTML error page as
/// the detail. The row that comes out is what is asserted.
/// </para>
/// </summary>
public sealed class CallLogRedactionTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Connection = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly RecordingStore _store = new();

    private CallJournal Journal() =>
        new(_store, new FrozenClock(DateTimeOffset.Parse("2026-03-04T10:00:00Z", CultureInfo.InvariantCulture)),
            NullLogger<CallJournal>.Instance);

    // ── The endpoint ────────────────────────────────────────────────────────

    /// <summary>
    /// The realistic leak: an adapter logs what it called, and what it called has the customer's
    /// phone number in a query parameter. The entity's <c>Sanitize</c> is the single choke point,
    /// and these cases are the ones an adapter actually produces.
    /// </summary>
    [Theory]
    // A relative path with a query string — the plain case.
    [InlineData("/party/v2/customers?phone=%2B2250708123456", "/party/v2/customers")]
    // A FULL absolute URL, which is what an adapter holding an HttpRequestMessage has in hand.
    [InlineData(
        "https://cbs.imf.ci/temenos/party/v2.0.0/customers?nationalId=CI-0012345&name=KOUASSI",
        "https://cbs.imf.ci/temenos/party/v2.0.0/customers")]
    // A fragment, which some gateways use to carry a tracking token.
    [InlineData("/holdings/accounts#selfie=abc123", "/holdings/accounts")]
    // Both at once: the cut is at the FIRST of the two, whichever comes first.
    [InlineData("/accounts?id=42#frag", "/accounts")]
    [InlineData("/accounts#frag?id=42", "/accounts")]
    public async Task The_stored_endpoint_never_carries_a_query_string_or_a_fragment(
        string given, string expected)
    {
        await Journal().RecordAsync(
            new CallContext(Tenant, Connection, "CreateCustomer", Endpoint: given),
            _ => Task.FromResult(IntegrationResult.Ok()),
            CancellationToken.None);

        var endpoint = _store.Rows.Should().ContainSingle().Which.Endpoint;

        endpoint.Should().Be(expected);
        endpoint.Should().NotContain("?").And.NotContain("#");
    }

    /// <summary>
    /// The case <c>Sanitize</c> cannot fix, stated so nobody mistakes the guarantee. An identifier
    /// inside the PATH survives, because dropping path segments would leave an endpoint nobody can
    /// recognise — <c>/customers/{id}/accounts</c> and <c>/customers/{id}/loans</c> would collapse
    /// into the same string and the stats endpoint would group two operations as one.
    ///
    /// <para>
    /// This is why the column is documented as "the endpoint called" and why the identifier that
    /// may legitimately appear there is an EXTERNAL system's reference, which is opaque outside
    /// that system — never a name, a phone or a document number. An adapter that templates the
    /// path (<c>/customers/{id}/accounts</c>) is better still, and the comparison below is what
    /// makes the difference visible to whoever writes the next adapter.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_identifier_in_a_path_segment_survives_and_that_is_the_documented_limit()
    {
        var journal = Journal();

        await journal.RecordAsync(
            new CallContext(Tenant, Connection, "GetAccounts", Endpoint: "/party/v2/customers/CBS-99812/accounts"),
            _ => Task.FromResult(IntegrationResult.Ok()), CancellationToken.None);

        await journal.RecordAsync(
            new CallContext(Tenant, Connection, "GetAccounts", Endpoint: "/party/v2/customers/{id}/accounts"),
            _ => Task.FromResult(IntegrationResult.Ok()), CancellationToken.None);

        _store.Rows[0].Endpoint.Should().Be(
            "/party/v2/customers/CBS-99812/accounts",
            "a path segment is kept: it is the external system's own opaque reference, and "
            + "stripping segments would merge distinct endpoints into one");

        _store.Rows[1].Endpoint.Should().Be(
            "/party/v2/customers/{id}/accounts",
            "and an adapter that templates the path leaves nothing identifying at all");
    }

    [Fact]
    public async Task A_long_endpoint_is_capped_to_the_column_width()
    {
        var huge = "/party/" + new string('a', 900) + "?secret=1";

        await Journal().RecordAsync(
            new CallContext(Tenant, Connection, "CreateCustomer", Endpoint: huge),
            _ => Task.FromResult(IntegrationResult.Ok()),
            CancellationToken.None);

        _store.Rows.Should().ContainSingle().Which.Endpoint.Should().HaveLength(
            500, "the column is varchar(500) and a write that overflows it is a lost row");
    }

    // ── The error code and the detail ───────────────────────────────────────

    /// <summary>
    /// The journal is fed exactly what a struggling gateway produces: a <c>Functional</c> failure
    /// whose <c>Detail</c> is a 50 KB HTML error page. Nothing of it may reach the row — not
    /// truncated, not summarised.
    /// </summary>
    [Fact]
    public async Task A_fifty_kilobyte_html_detail_reaches_no_column_of_the_row()
    {
        var page = HtmlErrorPage();
        page.Length.Should().BeGreaterThan(50_000, "the fixture has to be the real size to be the real test");

        await Journal().RecordAsync(
            new CallContext(Tenant, Connection, "CreateCustomer", Endpoint: "/party/v2/customers"),
            _ => Task.FromResult(IntegrationResult.Functional(IntegrationErrors.Duplicate, page)),
            CancellationToken.None);

        var row = _store.Rows.Should().ContainSingle().Which;

        row.ErrorFamily.Should().Be(ErrorFamily.Functional);
        row.ErrorCode.Should().Be(IntegrationErrors.Duplicate, "the code is kept; the prose is not");

        // Asserted over every string the row holds, not only over ErrorCode: the guarantee is
        // about the ROW, and a future column that started carrying the detail would have to break
        // this.
        foreach (var value in StringValuesOf(row))
        {
            value.Should().NotContain("<html", "no response body, anywhere in the row");
            value.Should().NotContain("KOUASSI", "and in particular no personal data from one");
            value.Should().NotContain("0708123456");
            value.Length.Should().BeLessThanOrEqualTo(500);
        }
    }

    /// <summary>
    /// The same page, this time passed as the error CODE — an adapter mapping an unrecognised 500
    /// has the body in hand, and <c>IntegrationResult.Technical(body)</c> compiles perfectly.
    /// </summary>
    [Fact]
    public async Task A_response_body_passed_as_the_error_code_is_collapsed_and_bounded()
    {
        await Journal().RecordAsync(
            new CallContext(Tenant, Connection, "CreateCustomer"),
            _ => Task.FromResult(IntegrationResult.Technical(HtmlErrorPage())),
            CancellationToken.None);

        var code = _store.Rows.Should().ContainSingle().Which.ErrorCode;

        code.Should().NotBeNull();
        code!.Length.Should().BeLessThanOrEqualTo(
            80, "error_code is varchar(80) and PostgreSQL would reject the row rather than trim it");
        code.Should().NotContain("\n").And.NotContain("\r").And.NotContain("\t",
            "a multi-line value would smuggle an apparent second row into a CSV export of this table");
        code.Should().NotContain("KOUASSI");
        code.Should().NotContain("0708123456");
    }

    [Theory]
    [InlineData("INTEGRATION_TIMEOUT", "INTEGRATION_TIMEOUT")]
    [InlineData("  INTEGRATION_TIMEOUT  ", "INTEGRATION_TIMEOUT")]
    [InlineData("CBS\r\nREFUSED\tTHE CALL", "CBS REFUSED THE CALL")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void BoundErrorCode_collapses_whitespace_and_control_characters(string? given, string? expected)
        => CallLogRedaction.BoundErrorCode(given).Should().Be(expected);

    [Fact]
    public void BoundErrorCode_never_ends_on_a_trailing_space_introduced_by_the_cut()
    {
        var code = CallLogRedaction.BoundErrorCode(new string('A', 79) + "   BBB");

        code.Should().NotBeNull();
        code.Should().NotEndWith(" ", "a trailing space would split one error code into two in a GROUP BY");
    }

    [Fact]
    public void DescribeDetail_reports_a_length_and_never_the_text()
    {
        var described = CallLogRedaction.DescribeDetail(HtmlErrorPage());

        described.Should().NotContain("<html").And.NotContain("KOUASSI");
        described.Should().Contain("chars", "the application log may say how much there was, never what");

        CallLogRedaction.DescribeDetail(null).Should().Be("none");
        CallLogRedaction.DescribeDetail(string.Empty).Should().Be("none");
    }

    // ── The shape of the table itself ───────────────────────────────────────

    /// <summary>
    /// The column set, pinned by reflection.
    ///
    /// <para>
    /// Every other test here proves that the current columns cannot hold personal data. This one
    /// is the only thing that keeps that true next quarter: adding a <c>CustomerReference</c>, a
    /// <c>RequestBody</c> or a <c>ResponseSnippet</c> to <see cref="IntegrationCallLog"/> is a
    /// one-line change that no compiler and no handler test would object to, and the table would
    /// quietly stop being exportable. It has to come past this list and past whoever reads the
    /// diff of it.
    /// </para>
    ///
    /// <para>
    /// If this test fails because a column was added deliberately: the question to answer in the
    /// pull request is not "is this field convenient" but "would I hand a CSV of this table to a
    /// regulator, and to the customer it describes".
    /// </para>
    /// </summary>
    [Fact]
    public void No_column_of_the_call_log_can_hold_personal_data()
    {
        string[] expected =
        [
            // Identity and partitioning.
            "Id", "At", "TenantId",
            // Opaque module-internal references — meaningless outside this tenant's schema.
            "ConnectionId", "CommandId",
            // What was asked, of which endpoint, and how it went.
            "Operation", "Endpoint", "HttpStatus", "DurationMs",
            // Why it failed, as a family and a bounded code.
            "ErrorFamily", "ErrorCode",
            // How to follow it into the other deployment's logs.
            "CorrelationId",
            // Inherited from AggregateRoot and mapped away (b.Ignore) — never a column.
            "DomainEvents",
        ];

        var actual = typeof(IntegrationCallLog)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToArray();

        actual.Should().BeEquivalentTo(
            expected,
            "the journal is the one table of this module meant to be handed to a controller; a "
            + "new property on it is a new thing in that export");
    }

    /// <summary>
    /// Every string the row carries, for the "nothing of the body is anywhere in the row"
    /// assertion. Reflection rather than a hand-written list for the same reason as the test
    /// above: a new column must be covered by default, not by somebody remembering.
    /// </summary>
    private static IEnumerable<string> StringValuesOf(IntegrationCallLog row) =>
        typeof(IntegrationCallLog)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.GetValue(row))
            .OfType<string>();

    /// <summary>
    /// A gateway's HTML error page, with a customer's name and phone in it — which is exactly what
    /// a CBS puts in the body of a duplicate rejection.
    /// </summary>
    private static string HtmlErrorPage() =>
        "<html><head><title>500 Internal Server Error</title></head><body>"
        + "<h1>Request rejected</h1>"
        + "<p>Customer KOUASSI Yao Jean-Baptiste (0708123456) already exists.</p>"
        + new string('x', 51_000)
        + "</body></html>";

    private sealed class RecordingStore : ICallLogStore
    {
        internal List<IntegrationCallLog> Rows { get; } = [];

        public Task AppendAsync(IntegrationCallLog row, CancellationToken ct)
        {
            Rows.Add(row);
            return Task.CompletedTask;
        }
    }

    private sealed class FrozenClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
