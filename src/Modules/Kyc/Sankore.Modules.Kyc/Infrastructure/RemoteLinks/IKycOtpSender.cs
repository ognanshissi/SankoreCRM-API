namespace Sankore.Modules.Kyc.Infrastructure.RemoteLinks;

/// <summary>
/// Delivers a one-time code to a customer's telephone (KRM-B-12).
///
/// <para>
/// <b>This platform has no SMS channel, and this interface is where that fact is recorded rather
/// than discovered.</b> M08 is email only — <c>EmailTemplate</c>, <c>QueueEmailAsync</c>, SMTP and
/// Brevo — and there is no provider, no queue, no per-tenant credential and no delivery receipt for
/// a text message anywhere in the solution. KRM-B-12 lists « M08 SMS » as a dependency that does
/// not exist; building it is a module-sized piece of work (a provider abstraction, credentials in
/// the vault, retries, receipts, a cost ceiling), not a clause of a Could-Have story.
/// </para>
///
/// <para>
/// So the OTP mechanism is complete — the code, its hash, its window, its attempt ceiling, the
/// second public endpoint and the session that waits behind it — and the DELIVERY is this port,
/// whose only registered implementation today is <see cref="UnavailableKycOtpSender"/>. The
/// consequence is deliberate and visible: <c>remote-otp</c> cannot be switched on while no sender
/// is available, because switching it on would lock every customer of that tenant out of their own
/// journey with nothing in the logs to say why.
/// </para>
///
/// <para>
/// The day a provider lands, it implements this and replaces one registration. Nothing else in the
/// journey changes.
/// </para>
/// </summary>
internal interface IKycOtpSender
{
    /// <summary>
    /// Whether a code can actually be delivered right now. Read by the settings slice BEFORE
    /// <c>remote-otp</c> is allowed on, so the refusal reaches an administrator at the moment they
    /// try rather than reaching their customers.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Sends <paramref name="code"/> to the client's principal telephone, and answers whether it
    /// was handed off.
    ///
    /// <para>
    /// The clear code is passed in and must not be stored, logged or echoed by any
    /// implementation: the invitation keeps only its hash. The recipient is named by
    /// <paramref name="clientId"/> and never by a number — M01 owns the encrypted telephone, the
    /// same arrangement <c>NotifyClientAsync</c> uses for the email address.
    /// </para>
    /// </summary>
    Task<bool> SendAsync(Guid tenantId, Guid clientId, string code, CancellationToken ct);
}

/// <summary>
/// The only implementation this solution ships: one that cannot send anything and says so.
///
/// <para>
/// Registered as the default so the mechanism has a dependency to resolve and the fail-closed
/// behaviour is the DEFAULT rather than a missing registration — an absent service would throw at
/// the first identification of a tenant that enabled the switch, which is the same outage with a
/// worse diagnosis.
/// </para>
/// </summary>
internal sealed class UnavailableKycOtpSender : IKycOtpSender
{
    public bool IsAvailable => false;

    public Task<bool> SendAsync(Guid tenantId, Guid clientId, string code, CancellationToken ct)
        => Task.FromResult(false);
}
