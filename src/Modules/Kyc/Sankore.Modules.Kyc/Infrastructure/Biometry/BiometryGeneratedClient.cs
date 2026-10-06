namespace Sankore.Modules.Kyc.Infrastructure.Biometry.Generated;

using System.Net.Http.Headers;

/// <summary>
/// The half of the generated client that cannot be generated: the per-call credentials.
///
/// <para>
/// Every call carries a bearer token taken from the vault for the TENANT being verified, and the
/// verification's correlation id so the two deployments' logs can be joined. NSwag has no hook for
/// either — a handler on the named <see cref="System.Net.Http.HttpClient"/> would have to find the
/// tenant through ambient state, and <c>DefaultRequestHeaders</c> are shared by every tenant using
/// that pooled client, which is how one tenant ends up calling with another's token.
/// </para>
///
/// <para>
/// Hence <c>/UseHttpRequestMessageCreationMethod:true</c> in the project file: the generated code
/// calls this factory for every request, and the instance is created per call around the pooled
/// HttpClient (the object is a thin wrapper — the connection pool is the HttpClient's).
/// </para>
/// </summary>
public partial class BiometryGeneratedClient
{
    /// <summary>Echoed by the service into its own logs; the two deployments correlate on it.</summary>
    internal const string CorrelationHeader = "X-Correlation-Id";

    /// <summary>Per-tenant service token. Never null by the time a call is made.</summary>
    internal string? ServiceToken { get; set; }

    internal string? CorrelationId { get; set; }

    private Task<HttpRequestMessage> CreateHttpRequestMessageAsync(CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage();

        if (!string.IsNullOrWhiteSpace(ServiceToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ServiceToken);

        if (!string.IsNullOrWhiteSpace(CorrelationId))
            request.Headers.TryAddWithoutValidation(CorrelationHeader, CorrelationId);

        return Task.FromResult(request);
    }
}
