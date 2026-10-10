namespace Sankore.Modules.Leads.Features.CustomerLink;

using Sankore.Modules.Customers.PublicApi;

/// <summary>
/// The single definition of when a lead may be attached to a client WITHOUT asking anybody
/// (BNF-B-22: "correspondance unique et exacte : rattachement automatique ; correspondance
/// partielle ou multiple : suggestion à confirmer par l'agent, jamais de rattachement silencieux").
///
/// <para>
/// It lives in one place because two callers need the same answer and must not drift: the capture
/// handler, which acts on it, and the suggestion endpoint, which tells the screen whether this
/// candidate is the confident one. A rule implemented twice is a rule that eventually attaches a
/// lead the suggestion screen had flagged as uncertain.
/// </para>
/// </summary>
internal static class CustomerMatchPolicy
{
    /// <summary>
    /// A match strong enough to act on alone. Two conditions, both necessary:
    ///
    /// <list type="number">
    /// <item>
    /// It is the ONLY candidate. Two clients sharing a phone is an ordinary situation — a family
    /// handset, a shop line — and picking between them is not a decision code should make.
    /// </item>
    /// <item>
    /// It matched on an identifier that identifies a PERSON. A national identity document does; an
    /// e-mail address does, in practice, for an individual. A phone number alone does NOT, for
    /// exactly the household reason above — so a lone phone match is a suggestion, never a link.
    /// </item>
    /// </list>
    /// </summary>
    internal static bool IsAutoAttachable(CustomerMatch match, int totalMatches)
    {
        if (totalMatches != 1)
            return false;

        return match.MatchedOn.Contains("identityDocument")
            || match.MatchedOn.Contains("email");
    }
}
