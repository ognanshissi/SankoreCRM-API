namespace Sankore.Modules.Administration.Domain;

/// <summary>
/// A tenant's SMS channel: which provider sends its text messages, under which sender identity,
/// and how many it may send in a month.
///
/// <para>
/// <b>Its own entity beside <see cref="TenantNotificationSettings"/> rather than more columns on
/// it.</b> The two concerns share nothing but the word "notification": an email provider has a
/// From address, a reply-to, a sending domain and an SMTP relay; an SMS provider has a sender id
/// and a per-message price. Folding them into one row would make every existing method —
/// <c>UpdateProvider</c>, <c>ResetToDefault</c>, <c>TryConsumeMonthlyQuota</c> — silently mean
/// "email", which is the kind of ambiguity that gets a quota consumed by the wrong channel. Two
/// rows, two slices, two cache keys, and the email path is untouched.
/// </para>
///
/// <para>
/// <b>Disabled is the default, and unlike the email channel there is no platform fallback.</b> An
/// email can always go out through the platform's own SMTP account; a text message cannot, because
/// somebody has to pay for it per message and no platform-wide account exists. A tenant that has
/// not configured a provider therefore has no SMS channel at all — which is what
/// <c>IKycOtpSender</c> reads to refuse enabling an SMS second factor (KRM-B-12).
/// </para>
/// </summary>
public sealed class TenantSmsSettings
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }

    /// <summary>
    /// <c>Disabled</c>, <c>Brevo</c> or <c>Twilio</c>. A string and not an enum for the reason
    /// <see cref="TenantNotificationSettings.ProviderType"/> is one: the value is readable in psql
    /// and is the same token the vault keys a credential under.
    /// </summary>
    public string ProviderType { get; private set; } = DisabledProvider;

    /// <summary>
    /// Who the message appears to come from.
    ///
    /// <para>
    /// The two providers mean different things by it and that is deliberately not modelled apart:
    /// Brevo takes an alphanumeric sender id of at most 11 characters ("SANKORE"), Twilio takes a
    /// phone number it has issued (<c>+2252012…</c>) or a messaging-service SID. One column,
    /// validated per provider, because a second one would be null on every row of the other kind
    /// and a screen would have to know which to show — which it has to know anyway.
    /// </para>
    /// </summary>
    public string? SenderId { get; private set; }

    /// <summary>
    /// Twilio's Account SID. Public-ish — it identifies the account, it does not authorise
    /// anything — so it sits in a column exactly as <see cref="TenantNotificationSettings.SmtpUsername"/>
    /// does, while the Auth Token goes to the vault. Null for Brevo, which authenticates on an API
    /// key alone.
    /// </summary>
    public string? AccountSid { get; private set; }

    /// <summary>
    /// The country calling code, digits only (<c>225</c> for Côte d'Ivoire), used to turn a
    /// telephone number stored in LOCAL form into the E.164 address a provider needs.
    ///
    /// <para>
    /// <b>It has to be configured, because nothing else in this platform knows it.</b> A tenant
    /// has a name, a logo and a default language and no country — and M01 stores numbers as they
    /// were dialled: <c>SensitiveValueNormalizer.NormalizePhone</c> deliberately does NOT infer an
    /// indicatif, because inferring one wrongly makes two different subscribers collide in a blind
    /// index. So a number captured as <c>0708091801</c> is unsendable until somebody says which
    /// country it belongs to, and the honest place to say it is the SMS channel's own row: this is
    /// a dialling question, not a company-identity one.
    /// </para>
    ///
    /// <para>
    /// Required to enable the channel. An institution whose customers are stored in local form
    /// would otherwise have a channel that silently cannot reach most of them — learning it at
    /// configuration time is the cheaper failure.
    /// </para>
    /// </summary>
    public string? DefaultCallingCode { get; private set; }

    /// <summary>
    /// A credential was stored in the vault for this provider. Never the credential.
    ///
    /// <para>
    /// Set only by the code that actually wrote to the vault — same arrangement as
    /// <see cref="TenantNotificationSettings.SetCredentialPresence"/>, so a tenant can never claim
    /// a provider whose secret was never stored.
    /// </para>
    /// </summary>
    public bool HasCredential { get; private set; }

    /// <summary>
    /// Messages allowed per calendar month, or null for no ceiling.
    ///
    /// <para>
    /// <b>Its own counter, separate from the email one, and that separation is the point.</b> A
    /// text message costs real money per unit where an email costs almost nothing, so the two
    /// budgets are not comparable — and sharing one counter would let a month of marketing emails
    /// exhaust the allowance that a customer's one-time code needs. A flood on one channel must
    /// not silence the other.
    /// </para>
    /// </summary>
    public int? MonthlyQuotaLimit { get; private set; }

    public int CurrentMonthUsageCount { get; private set; }
    public DateTimeOffset? CurrentMonthStartedAt { get; private set; }

    /// <summary>
    /// PostgreSQL <c>xmin</c>. The quota check and its increment are one operation on the
    /// aggregate; this is what makes that hold across processes, exactly as on the email row.
    /// </summary>
    public uint Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public Guid UpdatedBy { get; private set; }

    private TenantSmsSettings() { }

    /// <summary>No provider, no sender, no credential — a tenant with no SMS channel.</summary>
    public const string DisabledProvider = "Disabled";

    public static readonly string[] AllowedProviders = [DisabledProvider, "Brevo", "Twilio"];

    /// <summary>Providers that need an Account SID alongside their secret.</summary>
    public static bool UsesAccountSid(string providerType)
        => string.Equals(providerType, "Twilio", StringComparison.OrdinalIgnoreCase);

    public static TenantSmsSettings CreateDefault(Guid tenantId, Guid createdBy) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        ProviderType = DisabledProvider,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        UpdatedBy = createdBy,
    };

    /// <summary>
    /// Whether a text message can actually be sent for this tenant right now.
    ///
    /// <para>
    /// All three conditions, because each of them alone produces a channel that fails at send
    /// time: a provider must be chosen, a sender identity must exist (both providers reject a
    /// message without one), and the secret must be in the vault. This is what the KYC module
    /// reads before letting an administrator require an SMS second factor — the point being that
    /// they learn it, rather than their customers.
    /// </para>
    /// </summary>
    public bool CanSend =>
        !string.Equals(ProviderType, DisabledProvider, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(SenderId)
        && !string.IsNullOrWhiteSpace(DefaultCallingCode)
        && HasCredential
        && (!UsesAccountSid(ProviderType) || !string.IsNullOrWhiteSpace(AccountSid));

    public void UpdateProvider(
        string providerType, string? senderId, string? accountSid, string? defaultCallingCode,
        Guid updatedBy)
    {
        ProviderType = providerType;
        SenderId = string.IsNullOrWhiteSpace(senderId) ? null : senderId.Trim();
        AccountSid = string.IsNullOrWhiteSpace(accountSid) ? null : accountSid.Trim();

        // Digits only, and the leading plus dropped if somebody typed "+225": the code is
        // concatenated with a national number, so a stray plus in the middle of an E.164 address
        // is a message every provider rejects.
        DefaultCallingCode = string.IsNullOrWhiteSpace(defaultCallingCode)
            ? null
            : new string([.. defaultCallingCode.Where(char.IsAsciiDigit)]);

        UpdatedAt = DateTimeOffset.UtcNow;
        UpdatedBy = updatedBy;
    }

    public void SetCredentialPresence(bool hasCredential, Guid updatedBy)
    {
        HasCredential = hasCredential;
        UpdatedAt = DateTimeOffset.UtcNow;
        UpdatedBy = updatedBy;
    }

    /// <summary>
    /// Turns the channel off WITHOUT touching the sender id, the SID or the vault.
    ///
    /// <para>
    /// Deliberately non-destructive: an institution that suspends its SMS channel for a month —
    /// a budget decision, a provider dispute — must be able to turn it back on without
    /// re-entering a credential somebody may no longer have. <see cref="CanSend"/> is false either
    /// way, which is the only thing a sender asks.
    /// </para>
    /// </summary>
    public void Disable(Guid updatedBy)
    {
        ProviderType = DisabledProvider;
        UpdatedAt = DateTimeOffset.UtcNow;
        UpdatedBy = updatedBy;
    }

    public void SetMonthlyQuota(int? quotaLimit, Guid updatedBy)
    {
        MonthlyQuotaLimit = quotaLimit;
        UpdatedAt = DateTimeOffset.UtcNow;
        UpdatedBy = updatedBy;
    }

    /// <summary>
    /// Counts one message against this month's allowance, or refuses it. Check and increment are
    /// one operation — splitting them lets two senders both read "999 of 1000" and both send.
    /// </summary>
    public bool TryConsumeMonthlyQuota()
    {
        RollOverMonthIfNeeded();

        // Null limit means unlimited — still counted, so a tenant can see what it is spending.
        if (MonthlyQuotaLimit is { } limit && CurrentMonthUsageCount >= limit)
            return false;

        CurrentMonthUsageCount++;
        return true;
    }

    /// <summary>
    /// Lazily, on the first use of the month, for the reason the email row does it the same way:
    /// a scheduled reset that fails to run would silently block a tenant for a whole month.
    /// </summary>
    private void RollOverMonthIfNeeded()
    {
        var now = DateTimeOffset.UtcNow;

        if (CurrentMonthStartedAt is { } started
            && started.Month == now.Month
            && started.Year == now.Year)
        {
            return;
        }

        CurrentMonthUsageCount = 0;
        CurrentMonthStartedAt = now;
    }
}
