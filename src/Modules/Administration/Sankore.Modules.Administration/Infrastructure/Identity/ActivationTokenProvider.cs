namespace Sankore.Modules.Administration.Infrastructure.Identity;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sankore.Modules.Administration.Domain;

/// <summary>
/// Names shared by everything that issues, checks or consumes an activation token. They are
/// baked into the token's purpose chain, so changing either one invalidates every activation
/// link already in circulation.
/// </summary>
public static class ActivationTokens
{
    /// <summary>Key the provider is registered under in <see cref="IdentityOptions.Tokens"/>.</summary>
    public const string ProviderName = "SankoreActivation";

    /// <summary>Purpose string — distinct from Identity's "ResetPassword".</summary>
    public const string Purpose = "AccountActivation";
}

/// <summary>
/// Options for <see cref="ActivationTokenProvider"/>, separate from the framework-wide
/// <see cref="DataProtectionTokenProviderOptions"/> so account activation and password reset can
/// carry different lifespans.
///
/// They pull in opposite directions: an activation link is sent to someone who may not open their
/// mailbox for days, while a reset link is a live credential and should be short. Sharing one
/// setting means every extension granted to onboarding is silently also granted to password
/// reset.
/// </summary>
public sealed class ActivationTokenProviderOptions : DataProtectionTokenProviderOptions
{
    public ActivationTokenProviderOptions()
    {
        Name = ActivationTokens.ProviderName;
        TokenLifespan = IdentityTokenOptions.DefaultActivationLifespan;
    }
}

/// <summary>
/// The stock <see cref="DataProtectorTokenProvider{TUser}"/> bound to
/// <see cref="ActivationTokenProviderOptions"/>. Subclassing is the only way to give a
/// DataProtection-based provider its own options instance: the base type resolves
/// <c>IOptions&lt;DataProtectionTokenProviderOptions&gt;</c>, which every default provider shares.
/// </summary>
public sealed class ActivationTokenProvider(
    IDataProtectionProvider dataProtectionProvider,
    IOptions<ActivationTokenProviderOptions> options,
    ILogger<ActivationTokenProvider> logger)
    : DataProtectorTokenProvider<AppUser>(dataProtectionProvider, options, logger);
