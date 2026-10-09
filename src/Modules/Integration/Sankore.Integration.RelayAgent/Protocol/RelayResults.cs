namespace Sankore.Integration.RelayAgent.Protocol;

using System.Text.Json.Serialization;

// ---------------------------------------------------------------------------------------------
// The shapes that travel back inside RelayOrderResult.Payload. Same warning as RelayProtocol.cs:
// the SANKORE side of this contract does not exist yet, and these three records are what it has
// to mirror.
//
// They are the only place relayed data appears in this project. Each is built, serialised onto
// the session, and dropped — never persisted, never logged.
// ---------------------------------------------------------------------------------------------

/// <summary>
/// Answer to a <see cref="RelayOrderKind.HttpCall"/>.
///
/// <para>
/// <b>The status code is reported, not judged.</b> A 404 from a core banking system may mean
/// "no such customer" (a functional answer SANKORE must map) or a wrong path (a technical
/// fault), and this process knows nothing of our domain — it has no reference to the Integration
/// module by design — so it cannot tell. Classifying here would be guessing; the order therefore
/// SUCCEEDS whenever the local system answered at all, and <c>ErrorFamily</c> stays SANKORE's
/// decision, where the mapping tables are.
/// </para>
/// </summary>
public sealed record RelayHttpResult(
    [property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("contentType")] string? ContentType,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("bodyBase64")] string? BodyBase64);

/// <summary>
/// Answer to a <see cref="RelayOrderKind.SftpPut"/> — a byte count and nothing else, which is
/// all a deposit can report.
/// </summary>
public sealed record RelaySftpPutResult(
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("bytesWritten")] int BytesWritten);

/// <summary>
/// Answer to a <see cref="RelayOrderKind.SftpRead"/>. Always base64: a file collected from a
/// bank's exchange directory is as likely to be a fixed-width EBCDIC extract as a CSV, and
/// deciding its encoding here would corrupt it silently.
/// </summary>
public sealed record RelaySftpReadResult(
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("contentBase64")] string ContentBase64,
    [property: JsonPropertyName("bytesRead")] int BytesRead);

/// <summary>
/// Answer to a <see cref="RelayOrderKind.SqlView"/>: the column names once, then the rows as
/// arrays of strings.
///
/// <para>
/// Strings and not typed JSON values on purpose. A view's columns can be <c>numeric(19,4)</c>,
/// <c>timestamptz</c> or a domain type, and rendering each into a JSON number or date would make
/// the agent's locale and the agent's idea of precision part of the integration — the exact
/// failure the repository's spreadsheet importers were written to avoid. Invariant text here,
/// parsed on the SANKORE side where the mapping declares what each column means.
/// </para>
///
/// <para>
/// <paramref name="Truncated"/> says the declared row cap was reached, so the platform can tell
/// "there were no more rows" from "there were, and the agent refused to carry them".
/// </para>
/// </summary>
public sealed record RelaySqlViewResult(
    [property: JsonPropertyName("columns")] IReadOnlyList<string> Columns,
    [property: JsonPropertyName("rows")] IReadOnlyList<IReadOnlyList<string?>> Rows,
    [property: JsonPropertyName("truncated")] bool Truncated);
