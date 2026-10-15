namespace Sankore.Modules.Kyc.Features.RemoteInvitations;

using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Kyc.Domain;

/// <summary>
/// The remote-collection rules that apply to a file carrying NO product line.
///
/// <para>
/// <b>Why such a file exists at all.</b> Only one of the three ways a KYC file is opened knows a
/// product: <c>KycRequestedConsumer</c>, which comes from a lead conversion. The back-office
/// endpoint <c>POST kyc-files</c> — the one the KYC screen uses — and
/// <c>ClientCreatedKycConsumer</c> both open a file with <c>ProductLineId</c> null, because at
/// that moment nobody has chosen a product. Refusing remote collection on those meant that every
/// file opened from the KYC folder answered "the product does not allow it" about a product that
/// did not exist.
/// </para>
///
/// <para>
/// <b>Validation is MANUAL, and that is not a parameter.</b> Automatic approval is something an
/// institution switches on for a named product line, after deciding that line's risk is low
/// enough. A file nobody attached a product to has had no such decision taken about it, so the
/// machine must not decide either — <c>KycAutoApprovalPolicy</c> reads this as "not automatic"
/// and routes to a human, which is the only honest reading of an absent rule.
/// </para>
///
/// <para>
/// <b>And it grants the SIMPLIFIED tier.</b> A remote journey with no product rule produces the
/// lesser of the two; a validator who has read the file may still grant more by hand. What is
/// bounded here is what the unattended collection alone is worth.
/// </para>
///
/// <para>
/// One definition and not two: <see cref="KycRemoteEligibility"/> decides whether a link may be
/// sent and <c>KycRemoteJourney</c> decides what the customer is then asked for, and the two
/// disagreeing would mail somebody a link to a page that refuses to open — which is precisely
/// what happened while the journey refused a null product line on its own.
/// </para>
/// </summary>
internal static class KycRemoteDefaultPolicy
{
    /// <summary>
    /// How long the link lives. The same figure the product editor offers as its default, so an
    /// agent does not meet two different "default" lifetimes depending on where the file came from.
    /// </summary>
    internal const int LinkValidityHours = 72;

    /// <summary>
    /// The document types a customer may present. ALL of them, because the restriction exists so a
    /// product can narrow the choice and no product has asked for anything here — and because an
    /// empty list would leave the journey with no type to offer, which reads as a broken page
    /// rather than as a rule.
    /// </summary>
    private static readonly string[] EveryDocumentType =
    [
        nameof(KycDocumentType.Cni),
        nameof(KycDocumentType.Passport),
        nameof(KycDocumentType.Cedeao),
        nameof(KycDocumentType.Consulaire),
    ];

    /// <summary>
    /// The stand-in policy. <c>ProductLineId</c> is <c>Guid.Empty</c> and the three label fields
    /// are blank on purpose: every consumer already reads them defensively (<c>?.ProductName</c>),
    /// and inventing a product name here would put a product that does not exist into a customer's
    /// email.
    /// </summary>
    internal static KycRemotePolicyDto Instance { get; } = new(
        ProductLineId: Guid.Empty,
        ProductCode: string.Empty,
        ProductName: string.Empty,
        Category: string.Empty,
        IsActive: true,
        Enabled: true,
        ValidationMode: "Manual",
        RequiredTier: nameof(KycTier.Simplified),
        // Meaningless under a manual mode, and zero rather than a threshold nobody chose: a
        // non-zero floor here would look like a rule an institution set.
        MinAutoScore: 0,
        // EMPTY, and that is not an oversight: the journey's own `AlwaysRequired` — the identity
        // front and the selfie — is what a remote enrolment IS, and a product's list only ADDS to
        // it. So an empty list collects exactly the essentials and nothing invented.
        RequiredDocuments: [],
        AllowedDocumentTypes: EveryDocumentType,
        LinkValidityHours: LinkValidityHours);
}
