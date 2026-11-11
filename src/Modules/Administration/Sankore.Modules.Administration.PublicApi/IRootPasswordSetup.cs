namespace Sankore.Modules.Administration.PublicApi;

/// <summary>
/// The first password of an institution's root account (free trial, <c>docs/essai-gratuit-socle.md</c>,
/// lot 2). The starting-data load creates that account with a random password nobody knows; the
/// « Votre CRM est prêt » e-mail carries this link so the person who asked for the trial sets their
/// own. It opens the ordinary activation page (<c>/auth/account-activation</c>) and lasts as long as
/// an activation link (<c>Identity:ActivationTokenLifespan</c>, 7 days by default), not the two hours
/// of a password reset: the platform approves trials by hand, and the e-mail may be read late.
/// </summary>
public interface IRootPasswordSetup
{
    /// <summary>
    /// Null while the institution has no root account yet (its starting data is still loading):
    /// ask again later. Once the account has signed in, no link is issued any more
    /// (<see cref="RootPasswordSetupLink.AlreadySignedIn"/>): its owner already holds a password.
    /// </summary>
    Task<RootPasswordSetupLink?> IssueAsync(Guid tenantId, CancellationToken ct);
}

/// <param name="ActivationUrl">Null when <paramref name="AlreadySignedIn"/>.</param>
public sealed record RootPasswordSetupLink(
    Guid UserId,
    string Email,
    string? ActivationUrl,
    bool AlreadySignedIn,
    DateTimeOffset? ExpiresAt);
