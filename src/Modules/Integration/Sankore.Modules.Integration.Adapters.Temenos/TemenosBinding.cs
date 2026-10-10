namespace Sankore.Modules.Integration.Adapters.Temenos;

using Sankore.Modules.Integration.Domain;

/// <summary>
/// Everything a call needs to know about WHICH installation it is addressing: the tenant, the
/// connection row, and its Temenos settings already narrowed to the right type.
///
/// <para>
/// It exists because the ports take no connection. <c>ICbsCustomerPort.CreateCustomerAsync</c>
/// receives a payload and an idempotency key and nothing else — deliberately, so that a consumer
/// module never has to know a connection exists. The adapter is therefore what resolves the
/// tenant's active core banking connection, once per scope, and every operation then reads it
/// from here rather than re-querying.
/// </para>
///
/// <para>
/// <see cref="Settings"/> is non-null by construction: a connection whose settings are not
/// <c>TemenosSettings</c> never produces a binding, it produces a technical failure naming the
/// problem. That is what keeps every operation below free of null checks on configuration.
/// </para>
/// </summary>
internal sealed record TemenosBinding(IntegrationConnection Connection, TemenosSettings Settings)
{
    public Guid TenantId => Connection.TenantId;

    public Guid ConnectionId => Connection.Id;

    /// <summary>The token cache's key — tenant AND connection, never one of the two.</summary>
    public TemenosTokenScope Scope => new(TenantId, ConnectionId);

    /// <summary>
    /// The API version segment every path is built on. The connection's value when it has one,
    /// Transact's current major line otherwise — an installation that does not say is far likelier
    /// to be on the current line than to want no version segment at all, and a missing segment
    /// produces a 404 that reads like an absent customer.
    /// </summary>
    public string ApiVersion => string.IsNullOrWhiteSpace(Settings.ApiVersion)
        ? TemenosPaths.DefaultApiVersion
        : Settings.ApiVersion!.Trim('/');

    /// <summary>
    /// Absolute URL of a path, with the connection's base URL.
    ///
    /// <para>
    /// The base URL is forced to end in a slash before being combined. Without that,
    /// <c>new Uri(new Uri("https://host/transact"), "v2.0.0/party/customers")</c> silently drops
    /// the last segment and calls <c>https://host/v2.0.0/party/customers</c> — a 404 that looks
    /// like a wrong path rather than like a configuration detail.
    /// </para>
    /// </summary>
    public Uri Url(string path, string? query = null)
    {
        var baseUrl = Settings.BaseUrl.TrimEnd('/') + "/";
        var builder = new UriBuilder(new Uri(new Uri(baseUrl), path));

        if (!string.IsNullOrEmpty(query))
            builder.Query = query;

        return builder.Uri;
    }
}
