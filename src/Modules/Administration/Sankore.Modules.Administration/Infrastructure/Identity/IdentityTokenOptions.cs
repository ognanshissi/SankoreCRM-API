namespace Sankore.Modules.Administration.Infrastructure.Identity;

/// <summary>
/// Lifespans for the two link-bearing Identity tokens, bound from the <c>Identity</c>
/// configuration section.
///
/// Both accept the standard .NET TimeSpan form, so <c>"7.00:00:00"</c> is seven days and
/// <c>"02:00:00"</c> two hours. Environment variables use the usual double underscore:
/// <c>Identity__ActivationTokenLifespan</c>.
/// </summary>
public sealed class IdentityTokenOptions
{
    public const string SectionName = "Identity";

    /// <summary>Long by design: an activation email can sit unread for days.</summary>
    public static readonly TimeSpan DefaultActivationLifespan = TimeSpan.FromDays(7);

    /// <summary>Short by design: a reset link is a live credential.</summary>
    public static readonly TimeSpan DefaultPasswordResetLifespan = TimeSpan.FromHours(2);

    /// <summary>How long an account-activation link stays usable. Default 7 days.</summary>
    public TimeSpan ActivationTokenLifespan { get; set; } = DefaultActivationLifespan;

    /// <summary>
    /// How long a password-reset link stays usable. Default 2 hours. This one configures
    /// Identity's shared <c>DataProtectionTokenProviderOptions</c>, so it also covers email
    /// confirmation and change-email tokens — every default DataProtection-based provider.
    /// </summary>
    public TimeSpan PasswordResetTokenLifespan { get; set; } = DefaultPasswordResetLifespan;
}
