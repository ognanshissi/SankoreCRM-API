namespace Sankore.Modules.Kyc.Features.RemoteInvitations;

using Sankore.Modules.Kyc.Infrastructure.Storage;

/// <summary>
/// Document kinds in the words a CUSTOMER uses (KRM-B-10).
///
/// <para>
/// <b>Why the server holds these and not the front end</b>, which holds every other label in this
/// product. These go into an EMAIL: the message is rendered by M08 from a template and a data
/// payload, with no Angular anywhere, so "IdentityDocumentFront" is what the customer would read.
/// The equivalent list on the public page stays in the front end, where it belongs — the two are
/// not the same list and must not be made to share one.
/// </para>
///
/// <para>
/// French only, deliberately. Every message of this journey is French today — the templates seeded
/// for the customer's side have an English variant for completeness, and no deployment uses it
/// yet. When one does, the labels move into the template rather than being switched on a locale
/// here: a template that renders a list it cannot translate is the honest failure, and a
/// per-locale dictionary in C# is a translation nobody ever finds.
/// </para>
/// </summary>
internal static class KycRemoteDocumentLabels
{
    private static readonly Dictionary<KycDocumentKind, string> Labels = new()
    {
        [KycDocumentKind.IdentityDocumentFront] = "la face avant de votre pièce d'identité",
        [KycDocumentKind.IdentityDocumentBack] = "le verso de votre pièce d'identité",
        [KycDocumentKind.Selfie] = "votre photo de visage",
        [KycDocumentKind.ProofOfAddress] = "votre justificatif de domicile",
    };

    /// <summary>
    /// The labels for <paramref name="kindNames"/>, skipping anything this list does not know.
    ///
    /// <para>
    /// A kind with no label is DROPPED rather than rendered as its enum name: a customer told to
    /// send a "BeneficialOwnerDeclaration" has been told nothing, and a list that is short is
    /// better than a list that is gibberish. The email also carries the link, so the page itself
    /// names what is still missing — it reads the same journey the scope narrowed.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string> For(IEnumerable<string> kindNames)
        =>
        [
            .. kindNames
                .Select(n => Enum.TryParse<KycDocumentKind>(n, ignoreCase: true, out var k)
                    ? Labels.GetValueOrDefault(k)
                    : null)
                .Where(label => label is not null)
                .Select(label => label!)
        ];
}
