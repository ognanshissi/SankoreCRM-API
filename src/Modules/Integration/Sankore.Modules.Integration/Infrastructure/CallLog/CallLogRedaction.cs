namespace Sankore.Modules.Integration.Infrastructure.CallLog;

using System.Text;

/// <summary>
/// The rules that keep <c>integration_call_log</c> exportable (INT-08, criterion 2).
///
/// <para>
/// The journal exists so that a BCEAO or CIMA controller can be handed, call by call, what was
/// asked of a back-office and what it answered. Handing over a table means handing over every
/// row of it, so "no payload and no personal data in clear" cannot be a reviewer's good
/// intention — it has to be the only thing the writing path is capable of. These helpers are
/// that path: everything that reaches a column goes through one of them, and
/// <c>CallLogRedactionTests</c> feeds them what an adapter actually produces on a bad day
/// (a 50 KB HTML error page, a full absolute URL with a customer reference in the query string).
/// </para>
///
/// <para>
/// The deliberate omission is <c>IntegrationResult.Detail</c>. It is typed as
/// operator-facing prose, and prose is where a CBS puts the rejected value — "customer
/// KOUASSI/0708... already exists". There is no <c>detail</c> column precisely so that no
/// adapter can ever put one there by being helpful, and <see cref="DescribeDetail"/> is what the
/// application log gets instead: a length, never the text.
/// </para>
/// </summary>
internal static class CallLogRedaction
{
    /// <summary>
    /// Matches the <c>error_code</c> column (<c>varchar(80)</c>). Truncating here rather than
    /// letting PostgreSQL raise <c>22001 value too long</c> is the difference between a bounded
    /// row and a journal write that fails — and a failed journal write is a gap, which reads as
    /// an idle period.
    /// </summary>
    internal const int MaxErrorCodeLength = 80;

    /// <summary>Matches the <c>operation</c> column.</summary>
    internal const int MaxOperationLength = 60;

    /// <summary>
    /// Recorded when the operation delegate throws instead of returning a result. Local to the
    /// journal rather than added to <c>IntegrationErrors</c>: that class is the PublicApi
    /// vocabulary a consumer module and the front-end branch on, and this code is never returned
    /// to anybody — the original exception is rethrown. It exists only so the row says WHY it has
    /// no family of its own.
    /// </summary>
    internal const string UnhandledExceptionCode = "INTEGRATION_UNHANDLED_EXCEPTION";

    /// <summary>
    /// Recorded when the caller's own token cancels the call. A shutdown or an abandoned HTTP
    /// request is not the back-office's fault, so the family is <c>Transient</c> and never
    /// <c>Technical</c>: an administrator alerted for every deployment would stop reading the
    /// alerts.
    /// </summary>
    internal const string CancelledCode = "INTEGRATION_CALL_CANCELLED";

    /// <summary>
    /// A fresh correlation id. Same shape as M02's biometry correlation
    /// (<c>Guid.NewGuid().ToString("N")</c>): opaque, 32 hex characters, carries nothing about
    /// the customer, and short enough to survive being pasted into a support ticket.
    /// </summary>
    internal static string NewCorrelationId() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// Makes an adapter's error code safe for the column: single-line, collapsed, bounded.
    ///
    /// <para>
    /// The bound is the point. <c>IntegrationResult.Code</c> is documented as "a stable error
    /// code, never a sentence", but an adapter mapping an unknown 500 has the response body in
    /// hand and nothing stops it passing that body as the code. Control characters are stripped
    /// first so a multi-line body cannot smuggle a second apparent row into a CSV export of this
    /// table, and only then is the result cut to <see cref="MaxErrorCodeLength"/> — which is far
    /// too short to hold anything resembling a payload.
    /// </para>
    /// </summary>
    internal static string? BoundErrorCode(string? code) => Collapse(code, MaxErrorCodeLength);

    /// <summary>Same treatment for the logical operation name.</summary>
    internal static string BoundOperation(string operation)
        => Collapse(operation, MaxOperationLength) ?? string.Empty;

    /// <summary>
    /// What the application log is allowed to say about an error detail: that there was one, and
    /// how long it was.
    ///
    /// <para>
    /// Criterion 2 covers the application logs as much as the journal — Seq retains them, support
    /// reads them, and nobody redacts a log line after the fact. So the detail never appears in a
    /// log template, not even truncated: the first 80 characters of a CBS refusal are exactly
    /// where the refused name and account number live.
    /// </para>
    /// </summary>
    internal static string DescribeDetail(string? detail)
        => string.IsNullOrEmpty(detail) ? "none" : $"{detail.Length} chars (not logged)";

    /// <summary>
    /// Strips control characters, collapses runs of whitespace into a single space, trims, and
    /// truncates. No ellipsis: the value is matched and grouped by machines (the stats endpoint
    /// groups on <c>operation</c>), and a trailing "…" would split one operation into two.
    /// </summary>
    private static string? Collapse(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var builder = new StringBuilder(Math.Min(value.Length, maxLength));
        var lastWasSpace = false;

        foreach (var ch in value)
        {
            // Checked before appending anything, the separator included: a cap enforced only on
            // the non-whitespace branch overflows by one on a value whose cut falls on a space,
            // and one character over varchar(80) is PostgreSQL 22001 and a lost row. Stopping
            // here also means the remaining 49.9 KB of a response body is never even walked.
            if (builder.Length >= maxLength) break;

            if (char.IsControl(ch) || char.IsWhiteSpace(ch))
            {
                // Leading whitespace is dropped outright, so the trim is already done on the left
                // and only a trailing space can remain.
                if (builder.Length == 0 || lastWasSpace) continue;

                builder.Append(' ');
                lastWasSpace = true;
                continue;
            }

            builder.Append(ch);
            lastWasSpace = false;
        }

        if (lastWasSpace) builder.Length--;

        return builder.Length == 0 ? null : builder.ToString();
    }
}
