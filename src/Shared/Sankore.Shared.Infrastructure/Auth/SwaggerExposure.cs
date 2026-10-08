namespace Sankore.Shared.Infrastructure.Auth;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

/// <summary>
/// One place that decides whether a host serves its Swagger UI and OpenAPI document.
///
/// <para>
/// The default is unchanged from when the hosts tested <c>IsDevelopment()</c> inline: documentation
/// is served locally and nowhere else. <c>Swagger:Enabled</c> overrides it in both directions, so
/// a staging host can publish the UI without being renamed to Development — and when it does,
/// <see cref="SwaggerBasicAuthMiddleware"/> requires credentials for it.
/// </para>
/// </summary>
public static class SwaggerExposure
{
    public const string EnabledConfigKey = "Swagger:Enabled";

    /// <summary>
    /// <c>true</c> when this host should map the Swagger endpoints: <c>Swagger:Enabled</c> when
    /// set, otherwise Development only.
    /// </summary>
    public static bool IsEnabled(IConfiguration config, IHostEnvironment env)
        => config.GetValue<bool?>(EnabledConfigKey) ?? env.IsDevelopment();
}
