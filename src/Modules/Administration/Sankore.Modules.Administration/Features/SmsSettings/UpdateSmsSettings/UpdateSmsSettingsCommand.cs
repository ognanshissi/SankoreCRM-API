namespace Sankore.Modules.Administration.Features.SmsSettings.UpdateSmsSettings;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Configures the tenant's SMS channel.
/// </summary>
/// <param name="ProviderType"><c>Disabled</c>, <c>Brevo</c> or <c>Twilio</c>.</param>
/// <param name="SenderId">
/// Brevo's alphanumeric sender, or Twilio's number / messaging-service SID. Required for anything
/// but <c>Disabled</c>: both providers refuse a message without one, and accepting a provider with
/// no sender would produce a channel that looks configured and sends nothing.
/// </param>
/// <param name="AccountSid">Twilio's Account SID. Required for Twilio, ignored otherwise.</param>
/// <param name="DefaultCallingCode">
/// The country calling code — <c>225</c>, <c>221</c> — that local-form telephone numbers belong
/// to. Required to enable the channel: M01 stores numbers as they were dialled and this platform
/// has no other way to know the country, so without it most of a tenant's customers are
/// unreachable. A leading plus and any spacing are stripped.
/// </param>
/// <param name="Credential">
/// Brevo's API key, or Twilio's Auth Token. Written to the vault, never to a column.
///
/// <para>
/// <b>Left null, the stored credential is kept</b> — which is what lets an administrator change a
/// sender id without re-typing a key they may not have. Marked <c>SensitiveData</c> so
/// <c>AuditBehavior</c> does not serialise it into the audit trail, exactly as on the email
/// command.
/// </para>
/// </summary>
public sealed record UpdateSmsSettingsCommand(
    string ProviderType,
    string? SenderId,
    string? AccountSid,
    string? DefaultCallingCode,
    [property: SensitiveData] string? Credential) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "TenantSmsSettings";
    public string? ResourceId => null;
}
