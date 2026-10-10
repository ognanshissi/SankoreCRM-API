namespace Sankore.Modules.Integration.Adapters.Temenos;

using System.Net;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// Turns everything Transact can answer — a status code, an error envelope, a socket that never
/// opened — into exactly one <see cref="ErrorFamily"/> (INT-12, criterion 4).
///
/// <para>
/// This is the single most consequential file in the adapter, because the family is not
/// cosmetic: <c>Transient</c> makes the dispatcher try again, <c>Functional</c> parks the command
/// in a queue a human empties, <c>Technical</c> wakes an administrator and never retries. Get it
/// wrong in one direction and the platform hammers a system that refused on the merits; get it
/// wrong in the other and a customer's account is never opened because of a thirty-second
/// outage, with a rejection row that reads like a refusal by the bank.
/// </para>
///
/// <para>
/// So the rules are a table in one place rather than a judgement at each call site, and every
/// branch of it is pinned by a test.
/// </para>
/// </summary>
internal static class TemenosErrorClassifier
{
    /// <summary>
    /// Statuses that say "we learned nothing, ask again". 408 and 504 are a clock, 429 a quota,
    /// 502 and 503 a gateway or a maintenance window — none of them is an answer about the
    /// request itself.
    /// </summary>
    private static readonly int[] TransientStatuses = [408, 429, 502, 503, 504];

    /// <summary>
    /// Substrings that identify a refusal as a duplicate whatever status carried it. Matched
    /// case-insensitively on the code and on the message, because installations localise the
    /// message and keep the code — and some do the opposite.
    /// </summary>
    private static readonly string[] DuplicateMarkers =
        ["ALREADY EXISTS", "ALREADY-EXISTS", "DUPLICATE", "DEJA EXISTANT", "EXISTE DEJA"];

    /// <summary>Markers of a product the far end does not know (INT-13, criterion 2).</summary>
    private static readonly string[] UnknownProductMarkers =
        ["PRODUCT NOT FOUND", "INVALID PRODUCT", "UNKNOWN PRODUCT", "PRODUIT INCONNU", "NO.SUCH.PRODUCT"];

    /// <summary>
    /// Markers of a referenced record the far end does not hold, carried on a status other than
    /// 404 — T24 is fond of answering 400 with "RECORD NOT FOUND".
    /// </summary>
    private static readonly string[] NotFoundMarkers =
        ["RECORD NOT FOUND", "NO RECORD", "NOT FOUND", "DOES NOT EXIST", "INEXISTANT"];

    /// <summary>
    /// The value of <c>errorDetails[].type</c> that means "your value was refused" as opposed to
    /// "your request was malformed". A refused value is <see cref="ErrorFamily.Functional"/>; a
    /// malformed request is OUR bug and therefore <see cref="ErrorFamily.Technical"/>.
    /// </summary>
    private const string BusinessErrorType = "BUSINESS";

    /// <summary>
    /// Classifies an HTTP answer. Never returns a success: it is only called on a response that
    /// already failed, or that carried an error envelope on a 2xx.
    /// </summary>
    /// <param name="operation">
    /// The logical operation, put in the detail. An administrator told only "payload invalid" has
    /// nine operations to search.
    /// </param>
    public static IntegrationResult Classify(
        HttpStatusCode status, TemenosErrorBlock? error, string operation)
    {
        var code = (int)status;
        var detail = Describe(error, operation, code);
        var marker = MarkerText(error);

        // Availability first: a 503 carrying a maintenance message must not be read as a refusal
        // merely because the body happened to contain the word "not found".
        if (Array.IndexOf(TransientStatuses, code) >= 0)
        {
            return code switch
            {
                408 => IntegrationResult.Transient(IntegrationErrors.Timeout, detail),
                429 => IntegrationResult.Transient(IntegrationErrors.RateLimited, detail),
                _ => IntegrationResult.Transient(IntegrationErrors.Unavailable, detail),
            };
        }

        // 401/403 are Technical and NOT transient: our credentials or our scopes are wrong, and
        // the one thing that cannot fix them is calling again. The transport has already renewed
        // the token once before this classification is reached — see TemenosTransport.
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return IntegrationResult.Technical(IntegrationErrors.AuthenticationRefused, detail);

        // A referenced entity the far end does not hold is an ANSWER, so Functional: retrying a
        // lookup of something absent changes nothing, and a human has to decide what the dangling
        // reference means.
        if (status is HttpStatusCode.NotFound)
            return IntegrationResult.Functional(IntegrationErrors.ExternalEntityNotFound, detail);

        // 409 keeps its HTTP meaning. Functional, never transient: the record exists, so another
        // attempt refuses again until somebody looks.
        if (status is HttpStatusCode.Conflict)
            return IntegrationResult.Functional(IntegrationErrors.Duplicate, detail);

        // 400 and 422 — and a 2xx, which only reaches this method when the envelope itself
        // carried an error block. IRIS reports business refusals inside a successful response as
        // well as through status codes, and a 200 carrying "CUSTOMER ALREADY EXISTS" is a
        // duplicate however happy the transport was.
        if (code is 400 or 422 or (>= 200 and < 300))
            return ClassifyRefusal(error, marker, detail);

        // Every other 5xx. Transient on purpose: a 500 from a core banking system is its own bug
        // or its own overload, not a statement about our request, and parking the command in the
        // rejection queue would make a human re-enter a write that would have gone through on the
        // next attempt. It is the one family that costs nothing when wrong, because the attempt
        // budget stops the loop.
        if (code >= 500)
            return IntegrationResult.Transient(IntegrationErrors.Unavailable, detail);

        // Every other 4xx — 405, 415, 406. Each means we are speaking to the endpoint wrongly,
        // which is a deployment or a code fault and never something a retry or a clerk can fix.
        return IntegrationResult.Technical(IntegrationErrors.PayloadInvalid, detail);
    }

    /// <summary>
    /// 400 and 422, where Transact mixes "I refuse this value" with "I cannot read this request".
    ///
    /// <para>
    /// The brief's rule, applied in order of how much the answer tells us: a recognised business
    /// cause (duplicate, unknown product, absent record) is named for what it is; otherwise an
    /// envelope that identifies a FIELD or declares itself a business error is a refused value
    /// (<c>Functional</c>); and an envelope that says neither — or no envelope at all — is a
    /// malformed request, which is ours (<c>Technical</c>).
    /// </para>
    ///
    /// <para>
    /// The default leans Technical deliberately. A refusal we cannot read is more likely to be a
    /// wire-format mismatch of ours (see the warning at the top of <c>TemenosWire.cs</c>) than a
    /// considered business decision, and <c>Technical</c> is the family that puts it in front of
    /// whoever can compare the payload with the installation's document.
    /// </para>
    /// </summary>
    private static IntegrationResult ClassifyRefusal(
        TemenosErrorBlock? error, string marker, string detail)
    {
        if (ContainsAny(marker, DuplicateMarkers))
            return IntegrationResult.Functional(IntegrationErrors.Duplicate, detail);

        if (ContainsAny(marker, UnknownProductMarkers))
            return IntegrationResult.Functional(IntegrationErrors.UnknownProduct, detail);

        if (ContainsAny(marker, NotFoundMarkers))
            return IntegrationResult.Functional(IntegrationErrors.ExternalEntityNotFound, detail);

        var details = error?.ErrorDetails;

        var isRefusedValue = details is { Count: > 0 } && details.Exists(d =>
            !string.IsNullOrWhiteSpace(d.FieldName)
            || string.Equals(d.Type, BusinessErrorType, StringComparison.OrdinalIgnoreCase));

        return isRefusedValue
            ? IntegrationResult.Functional(IntegrationErrors.Rejected, detail)
            : IntegrationResult.Technical(IntegrationErrors.PayloadInvalid, detail);
    }

    /// <summary>
    /// A transport failure — DNS, TLS, a refused connection, a reset socket. Always
    /// <c>Transient</c>: we never reached the installation, so we learned nothing about the
    /// request, and the idempotency key plus the pre-create search are what make trying again
    /// safe.
    /// </summary>
    public static IntegrationResult Unreachable(string operation, string? reason)
        => IntegrationResult.Transient(
            IntegrationErrors.Unavailable,
            $"{operation}: the Temenos installation could not be reached ({reason ?? "no reason reported"}).");

    /// <summary>
    /// INT-09's breaker is open for this connection, so the call was never made.
    ///
    /// <para>
    /// Transient, and that classification is the whole point of the breaker: an open circuit says
    /// "this installation is not answering, come back later", which is precisely what the
    /// dispatcher's backoff is for. Classifying it <c>Functional</c> would park the command in the
    /// rejection queue for a human to look at, over an outage that will clear itself.
    /// </para>
    ///
    /// <para>
    /// It exists because Polly signals an open circuit by THROWING. Without this the exception
    /// escapes the port method, and opening the breaker — a protection — would turn a handled
    /// outage into an unhandled exception inside a Hangfire job. The module's rule is that a
    /// failure is a result, never an exception.
    /// </para>
    /// </summary>
    public static IntegrationResult CircuitOpen(string operation, Guid connectionId)
        => IntegrationResult.Transient(
            IntegrationErrors.CircuitOpen,
            $"{operation}: the circuit for connection {connectionId} is open; the call was not "
            + "attempted. See the module health check for its state.");

    /// <summary>Our own per-call budget expired. Transient, for the same reason.</summary>
    public static IntegrationResult TimedOut(string operation, TimeSpan budget)
        => IntegrationResult.Transient(
            IntegrationErrors.Timeout,
            $"{operation}: no answer from Temenos within {budget.TotalSeconds:0}s.");

    /// <summary>
    /// The answer did not match <c>TemenosWire.cs</c> — unparseable JSON, or a body missing the
    /// one field the caller cannot do without.
    ///
    /// <para>
    /// <c>Functional</c> rather than <c>Technical</c>, as <c>IntegrationErrors.UnexpectedResponse</c>
    /// documents: it is a refusal to guess. The detail names the operation, because the whole
    /// value of this code is to point at the one wire record to compare with the installation's
    /// own document.
    /// </para>
    /// </summary>
    public static IntegrationResult Unexpected(string operation, string? what = null)
        => IntegrationResult.Functional(
            IntegrationErrors.UnexpectedResponse,
            $"{operation}: the Temenos answer does not match the documented contract"
            + (what is null ? "." : $" ({what}). See TemenosWire.cs."));

    /// <summary>
    /// What goes in <see cref="IntegrationResult.Detail"/>: the operation, the status, and the
    /// installation's own codes — never its messages.
    ///
    /// <para>
    /// The message is dropped on purpose and this is not an oversight. A T24 refusal reads
    /// "CUSTOMER KOUASSI/0708... ALREADY EXISTS": it quotes the rejected value, which is the
    /// customer's name and phone number. INT-08's second criterion forbids personal data in the
    /// journal, and the detail is logged and shown to operators. The codes are stable tokens and
    /// carry nothing personal, and <see cref="MarkerText"/> — which does read the message — feeds
    /// only the classification and never a stored string.
    /// </para>
    /// </summary>
    private static string Describe(TemenosErrorBlock? error, string operation, int status)
    {
        var codes = error?.ErrorDetails?
            .Select(d => d.Code)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();

        return codes is { Count: > 0 }
            ? $"{operation}: Temenos answered HTTP {status} ({string.Join(", ", codes)})."
            : $"{operation}: Temenos answered HTTP {status}.";
    }

    /// <summary>
    /// The text the classification reads, upper-cased: codes AND messages, because installations
    /// differ on which of the two carries the cause. It is matched and discarded in the same
    /// expression — nothing derived from it is ever stored or logged.
    /// </summary>
    private static string MarkerText(TemenosErrorBlock? error)
    {
        if (error?.ErrorDetails is not { Count: > 0 } details)
            return string.Empty;

        return string.Join(
            " | ",
            details.Select(d => $"{d.Code} {d.Message}")).ToUpperInvariant();
    }

    private static bool ContainsAny(string haystack, string[] markers)
        => haystack.Length != 0
           && Array.Exists(markers, m => haystack.Contains(m, StringComparison.Ordinal));
}
