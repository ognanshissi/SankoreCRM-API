namespace Sankore.Modules.Notifications.PublicApi;

/// <summary>
/// Input for <see cref="INotificationsModule.QueueSmsAsync"/>.
/// </summary>
/// <param name="TemplateKey">
/// Identifies the SMS template — <c>kyc.remote-otp</c>, for instance. SMS templates are their own
/// set and do NOT share keys with the email templates: the same event often needs both, and a
/// shared key would make one the other's fallback.
/// </param>
/// <param name="RecipientPhone">
/// The destination in E.164 (<c>+2250708…</c>). <b>The caller normalises it</b>, because only the
/// caller knows the customer's country: a local eight-digit number means something different in
/// Abidjan and in Dakar, and a module guessing would send a one-time code to a stranger. M01's
/// <c>ICustomersModule.NotifyClientBySmsAsync</c> is the normal way in and does that work.
/// </param>
/// <param name="Module">Source module, for observability.</param>
/// <param name="Locale">BCP-47 locale for template selection. Defaults to French.</param>
/// <param name="TemplateData">Values injected into the body at render time.</param>
/// <param name="IdempotencyKey">
/// Caller-supplied. A duplicate is dropped, which on this channel is the difference between one
/// text message and two — and between one charge and two.
/// </param>
/// <param name="TenantId">Owns the message: resolves the provider and counts the allowance.</param>
/// <param name="ClientId">The client it is addressed to, when it is one — see <see cref="QueueEmailRequest"/>.</param>
public sealed record QueueSmsRequest(
    string TemplateKey,
    string RecipientPhone,
    string Module,
    string Locale,
    Dictionary<string, object> TemplateData,
    string IdempotencyKey,
    Guid TenantId,
    Guid? ClientId = null);
