namespace Sankore.Modules.Kyc.Infrastructure.RemoteLinks;

using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Notifications.PublicApi;

/// <summary>
/// Delivers a one-time code to a customer's telephone (KRM-B-12).
///
/// <para>
/// <b>Availability is per TENANT, and that is the whole point of the interface.</b> An SMS channel
/// is something each institution configures and pays for: a provider, a sender identity, a
/// credential in the vault and a calling code. A tenant that has not done that has no channel, and
/// requiring an SMS second factor there would put every one of its customers in front of a code
/// screen for a message that is never sent — with nothing in the logs to say why. So the settings
/// slice asks this question BEFORE letting <c>remote-otp</c> be switched on, and an administrator
/// learns it instead of their customers.
/// </para>
///
/// <para>
/// This shipped as a port with no implementation when the mechanism was written, because M08 was
/// email only. It now has one.
/// </para>
/// </summary>
internal interface IKycOtpSender
{
    /// <summary>
    /// Whether a code can actually be delivered for <paramref name="tenantId"/> right now:
    /// provider chosen, sender identity set, credential stored, calling code configured.
    ///
    /// <para>
    /// <b>False on an outage reading the configuration</b>, and that is the fail-closed direction:
    /// an administrator told "SMS is unavailable" retries, while one allowed to enable a second
    /// factor on a channel nobody could verify locks a tenant's customers out.
    /// </para>
    /// </summary>
    Task<bool> IsAvailableAsync(Guid tenantId, CancellationToken ct);

    /// <summary>
    /// Sends <paramref name="code"/> to the client's principal telephone, and answers whether it
    /// was queued.
    ///
    /// <para>
    /// The clear code is passed in and must not be stored, logged or echoed: the invitation keeps
    /// only its hash. The recipient is named by <paramref name="clientId"/> and never by a number
    /// — M01 owns the encrypted telephone and the conversion to a dialable address, the same
    /// arrangement <c>NotifyClientAsync</c> makes for the email.
    /// </para>
    /// </summary>
    Task<bool> SendAsync(Guid tenantId, Guid clientId, string code, CancellationToken ct);
}

/// <summary>
/// Sends the code through M08's SMS channel, over M01's send-to-client contract.
///
/// <para>
/// <b>Why it goes through M01 rather than straight to M08.</b> The telephone number is M01's
/// secret and the conversion from a stored local number to E.164 needs the tenant's calling code;
/// M01 does both. Calling <c>QueueSmsAsync</c> directly from here would mean this module asking
/// M01 for a number — which the contract deliberately does not offer — or guessing a country.
/// </para>
///
/// <para>
/// <b>The idempotency key is the invitation AND the code's own window.</b> Keyed on the invitation
/// alone, a customer asking for a second code would be silently given none: M08 drops a duplicate
/// key. Keyed on nothing, a retried request would charge the tenant twice. The expiry instant
/// changes with every freshly minted code and with nothing else, which is exactly the granularity
/// "one message per code" needs.
/// </para>
/// </summary>
internal sealed class SmsKycOtpSender(
    IAdministrationModule administration,
    ICustomersModule customers,
    ILogger<SmsKycOtpSender> logger)
    : IKycOtpSender
{
    public async Task<bool> IsAvailableAsync(Guid tenantId, CancellationToken ct)
    {
        try
        {
            var config = await administration.GetSmsConfigAsync(tenantId, ct);

            // CanSend is computed by M12's own entity — provider, sender, credential, calling
            // code. Not re-derived here: a second derivation is a second chance to get one of the
            // four wrong, and getting it wrong in the permissive direction locks a tenant out.
            return config?.CanSend ?? false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail closed. See the interface.
            logger.LogError(ex,
                "Could not read the SMS channel of tenant {TenantId}; treating the second factor "
                + "as unavailable.", tenantId);

            return false;
        }
    }

    public async Task<bool> SendAsync(
        Guid tenantId, Guid clientId, string code, CancellationToken ct)
    {
        // NotifyClientBySmsAsync never throws and answers false for every ordinary reason nothing
        // was sent — no telephone, no channel, a number that cannot be dialled.
        var queued = await customers.NotifyClientBySmsAsync(new NotifyClientBySmsRequest(
            TenantId: tenantId,
            ClientId: clientId,
            TemplateKey: KycOtpSmsTemplate.Key,
            // One message per code. See the class remarks for why the window is in the key.
            IdempotencyKey: $"{KycOtpSmsTemplate.Key}-{clientId:N}-{OtpWindowStamp()}",
            TemplateData: new Dictionary<string, object>
            {
                // The code and the minutes, and NOTHING else. A text message is readable on a
                // locked screen: naming the institution and the customer beside a code is how a
                // stolen handset becomes an enrolment. The seeded body is written to match.
                ["code"] = code,
                ["minutes"] = (int)KycRemoteOtpCodes.Validity.TotalMinutes,
            }), ct);

        if (!queued)
        {
            // No code in this line, ever. The failure is about the channel, and the clear value
            // exists inside one method by design.
            logger.LogWarning(
                "The remote-KYC second factor could not be queued for client {ClientId} "
                + "(tenant {TenantId}).", clientId, tenantId);
        }

        return queued;
    }

    /// <summary>
    /// A stamp that changes once per code window.
    ///
    /// <para>
    /// Derived from the clock rather than from the invitation's <c>OtpExpiresAt</c> because this
    /// sender is handed a code and a client, not a row — and it has to be stable across the
    /// single call that issues one. Truncated to the window length, so a code minted and a resend
    /// two seconds later share a key (correctly: that is a retry) while a resend after the window
    /// does not.
    /// </para>
    /// </summary>
    private static long OtpWindowStamp()
        => DateTimeOffset.UtcNow.ToUnixTimeSeconds()
           / (long)KycRemoteOtpCodes.Validity.TotalSeconds;
}

/// <summary>
/// The SMS template this module sends. Named here and seeded by M08 under the same key — a
/// mismatch produces a message with no body, which the SMS processor dead-letters rather than
/// sending.
/// </summary>
internal static class KycOtpSmsTemplate
{
    public const string Key = "kyc.remote-otp";
}
