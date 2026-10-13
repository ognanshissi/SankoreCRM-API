namespace Sankore.Modules.Kyc.Features.PublicRemote;

/// <summary>
/// The consent a customer gives before any camera opens, and the versioned text they were shown
/// (KRM-B-06).
///
/// <para>
/// <b>Why it is compiled in rather than a tenant-editable field.</b> What has to be provable, years
/// later and to a supervisor, is the exact wording this person agreed to. A row an administrator
/// can edit cannot answer that: edit it once and every past consent now points at text nobody
/// consented to. A constant with an explicit version can — <c>v1</c> is in the source, in the
/// history, and in the invitation row next to the timestamp.
/// </para>
///
/// <para>
/// <b>Why the version is sent back by the client and checked.</b> The page could have been loaded
/// before a deployment and submitted after it. Accepting whatever version arrives would record
/// agreement to the new text from somebody who read the old one; refusing an unknown version sends
/// them a reload, which costs one tap and keeps the record true.
/// </para>
///
/// <para>
/// It is deliberately NOT an email template or a localisation resource. Both are editable at
/// runtime by design, which is the one property this text must not have.
/// </para>
/// </summary>
internal static class KycRemoteConsentText
{
    /// <summary>
    /// Bump this and add a case below when the wording changes. Never edit a version in place: the
    /// invitations that point at it are the evidence that it was read.
    /// </summary>
    internal const string CurrentVersion = "v1";

    internal static bool IsKnownVersion(string? version)
        => string.Equals(version?.Trim(), CurrentVersion, StringComparison.Ordinal);

    /// <summary>
    /// The text for a locale, falling back to French — the default language of the deployments this
    /// serves, and the one <c>TenantInfo.DefaultLanguage</c> itself falls back to.
    /// </summary>
    internal static string For(string? locale)
        => locale?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true ? English : French;

    private const string French = """
        Pour vérifier votre identité, nous allons traiter la photographie de votre pièce
        d'identité et votre photographie de visage (selfie), ainsi que les données lues sur
        votre pièce : nom, prénoms, date de naissance, numéro et date d'expiration du document.

        Votre selfie est comparé automatiquement à la photographie de votre pièce d'identité.
        Cette comparaison constitue un traitement de données biométriques.

        Ces données sont utilisées uniquement pour vérifier votre identité dans le cadre de
        l'ouverture de votre dossier, conformément aux obligations de connaissance du client
        (LBC-FT) qui s'imposent à votre institution. Elles ne sont ni cédées ni utilisées à des
        fins commerciales. Elles sont conservées pendant la durée légale de conservation des
        pièces justificatives d'identité.

        Vous pouvez à tout moment demander à votre agence l'accès à ces données, leur
        rectification ou leur effacement dans les limites permises par la réglementation.

        Si vous préférez ne pas transmettre ces éléments en ligne, vous pouvez les présenter
        directement à votre agence : votre dossier suivra le même traitement.
        """;

    private const string English = """
        To verify your identity we will process a photograph of your identity document and a
        photograph of your face (selfie), together with the data read from your document: surname,
        given names, date of birth, document number and expiry date.

        Your selfie is automatically compared with the photograph on your identity document. That
        comparison is a processing of biometric data.

        This data is used only to verify your identity when opening your file, in accordance with
        the know-your-customer (AML/CFT) obligations that apply to your institution. It is neither
        sold nor used for marketing. It is kept for the legal retention period applicable to
        identity records.

        You may at any time ask your branch for access to this data, for its correction, or for its
        deletion within the limits the regulations allow.

        If you would rather not send these items online, you can present them at your branch
        instead: your file will be handled in exactly the same way.
        """;
}
