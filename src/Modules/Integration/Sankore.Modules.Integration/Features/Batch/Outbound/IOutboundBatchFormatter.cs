namespace Sankore.Modules.Integration.Features.Batch.Outbound;

using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// Renders the body of an outbound batch file — "dans le format défini par l'adaptateur"
/// (INT-24, criterion 2).
///
/// <para>
/// A seam rather than a hardcoded layout because the criterion says the ADAPTER defines the
/// format, and it genuinely does: Amplitude Legacy, Perfect Vision and ORASS each read a
/// different flat file, and the delimiter, the column order and the header line are properties of
/// the installation at the far end. An adapter assembly supplies its own by registering an
/// implementation; <see cref="DelimitedOutboundBatchFormatter"/> is registered with
/// <c>TryAdd</c> and is therefore only the fallback.
/// </para>
///
/// <para>
/// <b>No adapter ships one today.</b> The three batch-capable kinds are blocked on their
/// interface specifications (see docs/integration-module-plan.md §8: INT-28, INT-31, INT-32), so
/// a format invented here would be a guess dressed as a contract — and this repository has
/// already paid for that once, when M02's hand-written biometry records matched no field name in
/// the real document. The default below is therefore deliberately NOT a pretend CBS layout: it is
/// a faithful, self-describing dump of what the platform owes, which an integrator maps once.
/// </para>
/// </summary>
internal interface IOutboundBatchFormatter
{
    /// <summary>
    /// Extension of the produced file, with its dot. Part of the format, so the formatter owns it
    /// rather than the generator guessing <c>.csv</c> for a fixed-width layout.
    /// </summary>
    string FileExtension { get; }

    /// <summary>
    /// The file's text. The generator encodes it, checksums the ENCODED bytes and stores them —
    /// so a formatter must return the complete content including any header and trailer, and must
    /// not depend on the sequence number (see <c>OutboundBatchFileGenerator</c> for why the
    /// sequence is allocated after the content is rendered).
    /// </summary>
    string Render(OutboundBatchContext context, IReadOnlyList<OutboundBatchRecord> records);
}

/// <summary>
/// What a formatter may know about the file it is writing. Deliberately narrow: no tenant name,
/// no credentials, no connection object — a formatter renders records, it does not make routing
/// or authorisation decisions.
/// </summary>
/// <param name="ConnectionId">The connection the file is for.</param>
/// <param name="Kind">Which product is at the far end, for a kind-specific formatter.</param>
/// <param name="FieldSeparator">
/// From <c>BatchCapableSettings.FieldSeparator</c>. A semicolon by default, which is also what a
/// francophone Excel expects — and the separator is configurable precisely because a CBS that
/// wants a pipe or a tab is not negotiable.
/// </param>
/// <param name="CycleCutOff">The cut-off instant this file belongs to.</param>
internal sealed record OutboundBatchContext(
    Guid ConnectionId,
    IntegrationKind Kind,
    string FieldSeparator,
    DateTimeOffset CycleCutOff);

/// <summary>
/// One command, as the file carries it.
///
/// <para>
/// <see cref="Payload"/> arrives DECRYPTED — the generator is the only place in the module that
/// decrypts a command payload, and it does so because the whole purpose of the file is that the
/// CBS can read it. A formatter therefore handles personal data and must put nothing in a log.
/// </para>
/// </summary>
/// <param name="Payload">
/// Top-level fields of the command's payload, already flattened to text. Empty when the command
/// carries no payload (a <c>ReverseDebit</c> keyed only by its reference, for instance) or when
/// the stored payload could no longer be read.
/// </param>
internal sealed record OutboundBatchRecord(
    Guid CommandId,
    CommandType CommandType,
    string EntityType,
    Guid CrmId,
    string IdempotencyKey,
    DateTimeOffset CreatedAt,
    IReadOnlyDictionary<string, string> Payload);
