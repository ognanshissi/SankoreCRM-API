namespace Sankore.Modules.Administration.Tests.Features.NotificationSettings;

using FluentAssertions;
using Sankore.Modules.Administration.Domain;
using Xunit;

/// <summary>
/// The SMS channel's own invariants.
///
/// <para>
/// <c>CanSend</c> is the one computation every other module trusts — M08's resolver carries it,
/// M02 reads it to decide whether a second factor may be required at all — so what makes it false
/// is worth stating case by case. Each of the four conditions alone produces a channel that looks
/// configured and fails at send time, which is the failure this property exists to move earlier.
/// </para>
/// </summary>
public sealed class TenantSmsSettingsTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Actor = Guid.NewGuid();

    private static TenantSmsSettings Configured(string provider = "Brevo")
    {
        var settings = TenantSmsSettings.CreateDefault(Tenant, Actor);

        settings.UpdateProvider(
            provider,
            senderId: provider == "Twilio" ? "+22520123456" : "SANKORE",
            accountSid: provider == "Twilio" ? new string('a', 34) : null,
            defaultCallingCode: "225",
            Actor);

        settings.SetCredentialPresence(true, Actor);
        return settings;
    }

    [Fact]
    public void A_new_tenant_has_NO_SMS_channel()
    {
        // Unlike the email settings, whose default is the platform's own SMTP account. There is no
        // platform SMS account: somebody pays per message, so a tenant that has configured nothing
        // can send nothing.
        var settings = TenantSmsSettings.CreateDefault(Tenant, Actor);

        settings.ProviderType.Should().Be("Disabled");
        settings.CanSend.Should().BeFalse();
    }

    [Theory]
    [InlineData("Brevo")]
    [InlineData("Twilio")]
    public void A_fully_configured_channel_can_send(string provider)
    {
        Configured(provider).CanSend.Should().BeTrue();
    }

    [Fact]
    public void A_channel_with_no_credential_cannot_send()
    {
        var settings = TenantSmsSettings.CreateDefault(Tenant, Actor);
        settings.UpdateProvider("Brevo", "SANKORE", null, "225", Actor);

        settings.CanSend.Should().BeFalse();
    }

    [Fact]
    public void A_channel_with_no_sender_identity_cannot_send()
    {
        // Both providers refuse a message that carries none, so this would be a channel that
        // looks configured and dead-letters everything.
        var settings = TenantSmsSettings.CreateDefault(Tenant, Actor);
        settings.UpdateProvider("Brevo", senderId: null, accountSid: null, "225", Actor);
        settings.SetCredentialPresence(true, Actor);

        settings.CanSend.Should().BeFalse();
    }

    [Fact]
    public void A_channel_with_no_calling_code_cannot_send()
    {
        // The condition that is easiest to forget and hardest to diagnose: with a provider, a
        // sender and a key in place, every customer stored in LOCAL form is unreachable and
        // nothing says why. Required to enable, for exactly that reason.
        var settings = TenantSmsSettings.CreateDefault(Tenant, Actor);
        settings.UpdateProvider("Brevo", "SANKORE", null, defaultCallingCode: null, Actor);
        settings.SetCredentialPresence(true, Actor);

        settings.CanSend.Should().BeFalse();
    }

    [Fact]
    public void Twilio_without_an_account_SID_cannot_send()
    {
        var settings = TenantSmsSettings.CreateDefault(Tenant, Actor);
        settings.UpdateProvider("Twilio", "+22520123456", accountSid: null, "225", Actor);
        settings.SetCredentialPresence(true, Actor);

        settings.CanSend.Should().BeFalse();
    }

    [Fact]
    public void Brevo_needs_no_account_SID()
    {
        // Asserted so the Twilio rule above cannot be "tightened" into one that applies to both
        // and silently disables every Brevo tenant.
        var settings = Configured("Brevo");

        settings.AccountSid.Should().BeNull();
        settings.CanSend.Should().BeTrue();
    }

    [Fact]
    public void The_calling_code_is_stored_as_digits()
    {
        var settings = TenantSmsSettings.CreateDefault(Tenant, Actor);
        settings.UpdateProvider("Brevo", "SANKORE", null, "+225 ", Actor);

        // A stray plus in the middle of an E.164 address is a message every provider rejects.
        settings.DefaultCallingCode.Should().Be("225");
    }

    [Fact]
    public void Disabling_keeps_the_sender_the_SID_and_the_credential()
    {
        // Non-destructive on purpose: an institution suspending its channel for a month — a
        // budget decision, a provider dispute — must be able to turn it back on without
        // re-entering a key somebody may no longer have.
        var settings = Configured("Twilio");

        settings.Disable(Actor);

        settings.CanSend.Should().BeFalse();
        settings.ProviderType.Should().Be("Disabled");
        settings.SenderId.Should().Be("+22520123456");
        settings.AccountSid.Should().NotBeNull();
        settings.HasCredential.Should().BeTrue();
    }

    // ── The allowance ───────────────────────────────────────────────────────

    [Fact]
    public void An_unlimited_channel_still_counts_what_it_sends()
    {
        // So a tenant can see what it is spending. Null means no ceiling, not no accounting.
        var settings = Configured();

        settings.TryConsumeMonthlyQuota().Should().BeTrue();
        settings.TryConsumeMonthlyQuota().Should().BeTrue();

        settings.MonthlyQuotaLimit.Should().BeNull();
        settings.CurrentMonthUsageCount.Should().Be(2);
    }

    [Fact]
    public void A_ceiling_refuses_the_message_past_it()
    {
        var settings = Configured();
        settings.SetMonthlyQuota(2, Actor);

        settings.TryConsumeMonthlyQuota().Should().BeTrue();
        settings.TryConsumeMonthlyQuota().Should().BeTrue();
        settings.TryConsumeMonthlyQuota().Should().BeFalse();

        // And the refusal does not count: a tenant is not billed for what was not sent.
        settings.CurrentMonthUsageCount.Should().Be(2);
    }

    [Fact]
    public void Lowering_the_ceiling_below_what_is_already_spent_stops_the_channel()
    {
        // A platform administrator capping a tenant that has already overspent. It stops rather
        // than going negative or resetting — the month has to turn.
        var settings = Configured();
        settings.TryConsumeMonthlyQuota();
        settings.TryConsumeMonthlyQuota();
        settings.TryConsumeMonthlyQuota();

        settings.SetMonthlyQuota(1, Actor);

        settings.TryConsumeMonthlyQuota().Should().BeFalse();
        settings.CurrentMonthUsageCount.Should().Be(3);
    }

    [Fact]
    public void The_email_allowance_and_the_SMS_allowance_are_separate_counters()
    {
        // The reason this entity exists rather than more columns on the email row: a text message
        // costs real money per unit where an email costs almost nothing, so one shared counter
        // would let a month of emails exhaust the allowance a customer's one-time code needs.
        var email = TenantNotificationSettings.CreateDefault(Tenant, Actor);
        var sms = Configured();

        email.SetMonthlyQuota(1, Actor);
        sms.SetMonthlyQuota(1, Actor);

        email.TryConsumeMonthlyQuota().Should().BeTrue();
        email.TryConsumeMonthlyQuota().Should().BeFalse();

        // Exhausting the email allowance leaves the SMS one untouched.
        sms.TryConsumeMonthlyQuota().Should().BeTrue();
    }
}
