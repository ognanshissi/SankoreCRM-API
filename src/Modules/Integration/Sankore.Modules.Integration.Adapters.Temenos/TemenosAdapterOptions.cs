namespace Sankore.Modules.Integration.Adapters.Temenos;

using Microsoft.Extensions.Options;

/// <summary>
/// The adapter's own switches — the ones that are a property of OUR client and not of the
/// installation at the other end.
///
/// <para>
/// The division of labour with <c>TemenosSettings</c> matters: anything an administrator
/// configures PER CONNECTION (base URL, auth mode, company, API version, timeout, rate limit)
/// lives on the connection row and is editable through INT-03's endpoints. What is here is the
/// deployment's own behaviour, identical for every tenant, and has no business being per-tenant:
/// a renewal margin that one tenant could set to zero would make that tenant's every call race
/// its own token expiry.
/// </para>
/// </summary>
public sealed class TemenosAdapterOptions
{
    /// <summary>Bound from <c>Integration:Temenos</c>.</summary>
    public const string SectionName = "Integration:Temenos";

    /// <summary>
    /// How long before a token's stated expiry it is treated as expired.
    ///
    /// <para>
    /// Not a nicety. A token fetched with one second of life left is valid when we check it and
    /// expired by the time the request reaches the installation, and the call comes back 401 —
    /// which this adapter classifies as <c>AuthenticationRefused</c>, a TECHNICAL failure that
    /// is never retried and that wakes an administrator. The margin is what keeps a clock skew
    /// of a few seconds from looking like wrong credentials.
    /// </para>
    /// </summary>
    public TimeSpan TokenRenewalMargin { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long a token with no <c>expires_in</c> is cached. <c>expires_in</c> is optional in
    /// RFC 6749, and a token cached for ever would keep working until an installation rotated its
    /// signing key — at which point every tenant's calls would fail at once, in production, with
    /// no renewal having ever been exercised.
    /// </summary>
    public TimeSpan FallbackTokenLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Per-call budget when the connection's own <c>TimeoutSeconds</c> is zero. Applied on a
    /// linked cancellation token, which is what lets our own expiry be told apart from the
    /// caller giving up — <c>HttpClient.Timeout</c> reports both as the same exception.
    /// </summary>
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Budget for a token request. Shorter than a business call on purpose: an authorisation
    /// server that is slow is unavailable, and spending the whole call budget on authentication
    /// leaves nothing for the operation the caller actually asked for.
    /// </summary>
    public TimeSpan TokenTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Rows asked for per page of transactions.</summary>
    public int TransactionPageSize { get; set; } = 100;

    /// <summary>
    /// Pages the monthly-flow aggregation will walk before refusing.
    ///
    /// <para>
    /// A bound and not a convenience: the flow is computed by walking a month of history, and a
    /// pager that keeps handing back its own token would otherwise spin a Hangfire job for ever
    /// with nothing in the logs. Hitting the bound is reported as
    /// <c>INTEGRATION_UNEXPECTED_RESPONSE</c>, because a month that does not fit in this many
    /// pages means the page token is not advancing.
    /// </para>
    /// </summary>
    public int MaxPagesPerAggregation { get; set; } = 200;

    /// <summary>
    /// Cap on a single response body. An installation behind a misconfigured gateway answers an
    /// HTML page, and reading an unbounded one into memory is how one bad route exhausts the
    /// process.
    /// </summary>
    public int MaxResponseBytes { get; set; } = 8 * 1024 * 1024;
}

/// <summary>
/// Validated at start-up, so a nonsensical value is a boot failure naming the key rather than a
/// surprise on the first customer creation.
/// </summary>
internal sealed class TemenosAdapterOptionsValidator : IValidateOptions<TemenosAdapterOptions>
{
    public ValidateOptionsResult Validate(string? name, TemenosAdapterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        // Negative is nonsense; zero is a deliberate, documented choice ("renew only once the
        // token has actually expired"), so it is allowed and only the negative case is refused.
        if (options.TokenRenewalMargin < TimeSpan.Zero)
            failures.Add($"{TemenosAdapterOptions.SectionName}:TokenRenewalMargin cannot be negative.");

        if (options.FallbackTokenLifetime <= TimeSpan.Zero)
            failures.Add($"{TemenosAdapterOptions.SectionName}:FallbackTokenLifetime must be positive.");

        if (options.DefaultTimeout <= TimeSpan.Zero)
            failures.Add($"{TemenosAdapterOptions.SectionName}:DefaultTimeout must be positive.");

        if (options.TokenTimeout <= TimeSpan.Zero)
            failures.Add($"{TemenosAdapterOptions.SectionName}:TokenTimeout must be positive.");

        if (options.TransactionPageSize <= 0)
            failures.Add($"{TemenosAdapterOptions.SectionName}:TransactionPageSize must be positive.");

        if (options.MaxPagesPerAggregation <= 0)
            failures.Add($"{TemenosAdapterOptions.SectionName}:MaxPagesPerAggregation must be positive.");

        if (options.MaxResponseBytes <= 0)
            failures.Add($"{TemenosAdapterOptions.SectionName}:MaxResponseBytes must be positive.");

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
