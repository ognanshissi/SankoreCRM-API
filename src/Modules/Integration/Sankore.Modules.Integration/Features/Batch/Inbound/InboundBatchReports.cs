namespace Sankore.Modules.Integration.Features.Batch.Inbound;

/// <summary>
/// What one inbound poll did, file by file and line by line.
///
/// <para>
/// A report rather than exceptions, and that is the instruction INT-25 inherits from the three
/// importers of this repository: <b>a malformed line is a reported line, never an exception that
/// abandons the file</b>. An acknowledgement file is a day's worth of closures for a tenant; one
/// line a sender wrote badly must not leave the other two hundred commands open, waiting for a
/// file that will never be re-sent.
/// </para>
///
/// <para>
/// It is also what the tests assert on. The jobs log a summary and the per-line detail; nothing
/// here reaches an HTTP response, so the detail may name a command id and an external id — but
/// never a payload, like every operator-facing string in this module.
/// </para>
/// </summary>
internal sealed class InboundBatchPollReport
{
    /// <summary>Names the transport listed in the inbound directory.</summary>
    public int Listed { get; set; }

    /// <summary>Files read, verified and applied.</summary>
    public int Processed { get; set; }

    /// <summary>Files whose sequence was already recorded. Criterion 1's "already processed".</summary>
    public int Skipped { get; set; }

    /// <summary>Files refused on their checksum or their sequence. Nothing of them was applied.</summary>
    public int Failed { get; set; }

    /// <summary>Files whose envelope could not be read at all, so not attributable to a sequence.</summary>
    public int Unreadable { get; set; }

    /// <summary>Acknowledgement lines that closed a command, either way.</summary>
    public int CommandsClosed { get; set; }

    /// <summary>Of those, the ones that moved to <c>Succeeded</c>.</summary>
    public int CommandsSucceeded { get; set; }

    /// <summary>Of those, the ones that moved to <c>Rejected</c>.</summary>
    public int CommandsRejected { get; set; }

    /// <summary>Customers an extraction named and the projector was asked to refresh.</summary>
    public int CustomersProjected { get; set; }

    /// <summary>Overdue acknowledgements alerted on this run (criterion 3).</summary>
    public int AckOverdueAlerts { get; set; }

    public List<InboundFileReport> Files { get; } = [];

    public List<InboundLineReport> Lines { get; } = [];
}

/// <summary>One file's outcome.</summary>
internal sealed record InboundFileReport(
    string FileName,
    long? SequenceNo,
    InboundFileOutcome Outcome,
    string? Code,
    string? Detail);

/// <summary>
/// One line that could not be applied, with the line number as it appears IN THE FILE — the
/// header is line 1, so the first record is line 2. An operator opening the file in an editor
/// must land on the line the report names.
/// </summary>
internal sealed record InboundLineReport(string FileName, int FileLine, string Code, string? Detail);

internal enum InboundFileOutcome
{
    /// <summary>Verified and applied. Archived out of the polling directory.</summary>
    Processed,

    /// <summary>Its sequence is already recorded. Nothing was read, nothing was applied.</summary>
    Skipped,

    /// <summary>Refused and recorded as <c>Failed</c>. Left in place for a human.</summary>
    Failed,

    /// <summary>No readable envelope, so no sequence to record it under. Left in place.</summary>
    Unreadable
}

/// <summary>
/// Codes this slice reports with.
///
/// <para>
/// <b>Not in <c>IntegrationErrors</c> on purpose.</b> That class is the PublicApi contract a
/// consumer module and the front-end branch on; these are line-level diagnostics of one file,
/// read by whoever opens the Hangfire log or the batch-file row. The two file-level refusals that
/// ARE part of the contract — a bad checksum and an out-of-order sequence — use
/// <c>IntegrationErrors.BatchChecksumMismatch</c> and
/// <c>IntegrationErrors.BatchSequenceOutOfOrder</c>, which already exist.
/// </para>
/// </summary>
internal static class InboundBatchCodes
{
    /// <summary>The envelope is not one of ours, or not readable.</summary>
    public const string HeaderUnreadable = "BATCH_HEADER_UNREADABLE";

    /// <summary>The line does not have the fields its kind requires.</summary>
    public const string LineMalformed = "BATCH_LINE_MALFORMED";

    /// <summary>No <c>Batched</c> command of this tenant and connection carries that id.</summary>
    public const string CommandUnknown = "BATCH_COMMAND_UNKNOWN";

    /// <summary>
    /// The command is no longer waiting for an acknowledgement. A replayed file, or two
    /// acknowledgements for one command: reported and skipped, never an error — the second
    /// delivery of an at-least-once file is normal.
    /// </summary>
    public const string CommandNotAwaitingAck = "BATCH_COMMAND_NOT_AWAITING_ACK";

    /// <summary>A success line with no external identifier. Refused: see the applier.</summary>
    public const string ExternalIdMissing = "BATCH_EXTERNAL_ID_MISSING";

    /// <summary>A refusal line with no reason code.</summary>
    public const string ReasonMissing = "BATCH_REASON_MISSING";

    /// <summary>
    /// The external identifier contradicts a reference we already hold. The command is left
    /// <c>Batched</c> rather than closed on a contradiction.
    /// </summary>
    public const string ReferenceConflict = "BATCH_REFERENCE_CONFLICT";

    /// <summary>An extraction names an external customer this connection has no reference for.</summary>
    public const string CustomerNotReferenced = "BATCH_CUSTOMER_NOT_REFERENCED";

    /// <summary>
    /// A <c>Batched</c> command's outbound file was never acknowledged within the connection's
    /// <c>AckTimeoutHours</c> (criterion 3). Recorded on the file's <c>FailureDetail</c>.
    /// </summary>
    public const string AckOverdue = "BATCH_ACK_OVERDUE";
}
