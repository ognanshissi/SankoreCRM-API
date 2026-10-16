namespace Sankore.Modules.Integration.Adapters.Ringover;

using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// Why a provider call failed, and therefore what the platform does next (TEL-B-04).
///
/// <para>
/// <b>This one is implemented in full, and the reason is worth stating: HTTP is not Ringover's
/// vocabulary.</b> 401, 403, 429 and the 5xx range mean the same thing whoever serves them, the
/// plan states the mapping outright — « classification 401/403 → <c>Technical</c>, 429/5xx →
/// <c>Transient</c>, numéro invalide → <c>Functional</c> » — and nothing here is a guess about a
/// document. What the test account is still needed for is narrower and recorded as open question
/// 3: whether an invalid number is refused on the request at all, or accepted and failed later on
/// a webhook.
/// </para>
///
/// <para>
/// <b>Why the three families and not an HTTP status.</b> <c>ErrorFamily</c> decides what happens
/// next, and the three outcomes are genuinely different actions: <c>Transient</c> is retried with
/// backoff, <c>Functional</c> goes to a human because the provider answered and said no, and
/// <c>Technical</c> alerts an administrator because our own configuration is wrong. Getting one
/// wrong is not cosmetic. A 401 classified <c>Transient</c> makes the dispatcher hammer the
/// provider with a key it has already refused, which is how an account gets rate-limited and then
/// suspended for behaviour that looks like an attack. A 429 classified <c>Technical</c> does the
/// opposite: it parks a call that would have succeeded a second later and pages somebody at
/// night.
/// </para>
///
/// <para>
/// <b>Pure and static.</b> No clock, no I/O, no logger — it is a function of a status code and a
/// token, and the caller owns the journal row.
/// </para>
/// </summary>
public static class RingoverErrorClassification
{
    /// <summary>
    /// The family for one failed response.
    ///
    /// <para>
    /// Ordered from the most specific to the most general, and the order is the rule rather than
    /// an optimisation: a 429 is <c>Transient</c> even though it is a 4xx, and an authentication
    /// refusal is <c>Technical</c> even though it is one too — so a plain "4xx means Functional"
    /// would be wrong for both of the cases that matter most.
    /// </para>
    /// </summary>
    /// <param name="httpStatus">
    /// The response status. A value outside 100–599 — which is what a transport exception is
    /// reduced to, since there was no response — is <see cref="ErrorFamily.Transient"/>: a
    /// connection that never completed is exactly what a retry is for.
    /// </param>
    /// <param name="providerCode">
    /// The provider's own error token, when one is present in the body. <b>Consulted only to
    /// NARROW a family, never to widen one</b> (see <see cref="FunctionalCodes"/>), and empty
    /// today — so every answer below is reached from the status alone, and no token is invented.
    /// </param>
    public static ErrorFamily Classify(int httpStatus, string? providerCode = null)
    {
        // Our key, or our permissions. Never retried: a retry cannot obtain a credential, and
        // repeating a refused key is what gets an account suspended.
        if (httpStatus is 401 or 403) return ErrorFamily.Technical;

        // Asked to slow down. The one 4xx that IS transient, and the reason this method is not a
        // range check.
        if (httpStatus == 429) return ErrorFamily.Transient;

        // The provider is unwell. Includes 501 and 505 on purpose: in a provider's hands those
        // are usually a gateway in front of a service that is being deployed, not a permanent
        // statement about the operation — and the dispatcher's backoff is bounded, so the cost of
        // being wrong in this direction is a few retries rather than a parked call.
        if (httpStatus >= 500) return ErrorFamily.Transient;

        // A request the provider refused on its content: an invalid number, a user who does not
        // exist, a call that cannot be placed to that destination. It answered and said no, so a
        // human looks at it — retrying an invalid number would ring nobody, forever.
        //
        // 404 is in here rather than Technical, and it is the one arm worth arguing about. On a
        // READ it is "this call id does not exist at the provider", which is a fact about the
        // data; classified Technical it would alert an administrator every time the catch-up
        // sweep asked about a call the provider has since aged out of its history.
        if (httpStatus is >= 400 and < 500) return ErrorFamily.Functional;

        // No response at all — a timeout, a DNS failure, a reset. Reduced to a status outside the
        // valid range by the caller rather than carried as an exception, so there is one
        // classification path and not two.
        if (httpStatus is < 100 or > 599) return ErrorFamily.Transient;

        // A 1xx/2xx/3xx reached a method that only sees failures. Technical, because the caller
        // has a bug: treating it as Transient would retry a SUCCESS, and in this family that
        // means ringing somebody's telephone a second time.
        return ErrorFamily.Technical;
    }

    /// <summary>
    /// Provider tokens that force <see cref="ErrorFamily.Functional"/> regardless of the status.
    ///
    /// <para>
    /// <b>Empty until a test account is obtained (open question 3), and deliberately one-way.</b>
    /// A token may only ever narrow a <c>Transient</c> to a <c>Functional</c> — a provider that
    /// returns 500 for a number it has in fact rejected on the merits, which is a real shape —
    /// and never the reverse. The asymmetry is the safety property: a wrong entry here costs a
    /// retry that was never going to succeed, while a token that promoted a <c>Functional</c> to
    /// <c>Transient</c> would turn a permanent refusal into an infinite retry.
    /// </para>
    /// </summary>
    public static readonly IReadOnlySet<string> FunctionalCodes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The family, with the provider's token allowed to narrow it.
    ///
    /// <para>
    /// Separate from <see cref="Classify"/> rather than folded into it, so that the status-only
    /// classification — the half that needs nothing from anybody — stays callable and testable on
    /// its own.
    /// </para>
    /// </summary>
    public static ErrorFamily ClassifyWithToken(int httpStatus, string? providerCode)
    {
        var family = Classify(httpStatus, providerCode);

        if (family != ErrorFamily.Transient) return family;

        var token = providerCode?.Trim();

        return !string.IsNullOrEmpty(token) && FunctionalCodes.Contains(token)
            ? ErrorFamily.Functional
            : family;
    }
}
