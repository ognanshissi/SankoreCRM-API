namespace Sankore.Modules.Kyc.Features.PublicRemote;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Infrastructure.Settings;
using Sankore.Modules.Kyc.Infrastructure.Storage;

/// <summary>
/// What the customer still has to do, as the product's own policy defines it (KRM-B-06).
///
/// <para>
/// <b>The page holds no business rule, and this is why.</b> Which identity documents are accepted,
/// whether a proof of address is needed, whether a back is required — all of it comes from the
/// product line the lead was converted on, and all of it can change between a conversion and an
/// upload. A front end that decided any of it would decide it differently from the server the day
/// an administrator edited a product, and the customer would be refused at the end of a journey the
/// page had told them was complete.
/// </para>
///
/// <para>
/// One service because three endpoints need the same answer: the session read renders it, the upload
/// validates against it, and the submission refuses on it. Computed every time rather than cached on
/// the invitation, for the reason the agent's panel recomputes its own refusal reason — the inputs
/// move on their own.
/// </para>
/// </summary>
internal sealed class KycRemoteJourney(
    KycDbContext db,
    IAdministrationModule administration,
    IKycSettings settings)
{
    /// <summary>
    /// The kinds the customer uploads, in the order the journey presents them.
    ///
    /// <para>
    /// The front and the selfie are NOT read from the product's <c>RequiredDocuments</c>: they are
    /// what a remote enrolment IS, and a product that omitted them would collect nothing at all.
    /// The product's list adds to these; it cannot take them away.
    /// </para>
    /// </summary>
    internal static readonly KycDocumentKind[] AlwaysRequired =
    [
        KycDocumentKind.IdentityDocumentFront,
        KycDocumentKind.Selfie,
    ];

    /// <summary>
    /// Document types that have a back worth photographing.
    ///
    /// <para>
    /// A passport's data page is the whole document — asking for its back produces a photograph of
    /// a blank cover, which the OCR then rejects and the customer retries. The other three are
    /// cards with the issuing data on the reverse.
    /// </para>
    /// </summary>
    internal static bool HasBack(KycDocumentType type) => type is not KycDocumentType.Passport;

    internal sealed record JourneyState(
        KycRemotePolicyDto Policy,
        IReadOnlyList<KycDocumentType> AllowedDocumentTypes,
        IReadOnlyList<KycDocumentKind> RequiredKinds,
        IReadOnlyDictionary<KycDocumentKind, bool> Uploaded,
        KycDocumentType? ChosenType,
        IReadOnlyDictionary<string, int> RetriesUsed,
        int MaxRetries,
        bool HasConsented)
    {
        /// <summary>
        /// Kinds the product requires that are not on file yet — what the submission refuses on
        /// and what the page's send button waits for.
        /// </summary>
        internal IReadOnlyList<KycDocumentKind> Missing =>
            [.. RequiredKinds.Where(k => !Uploaded.GetValueOrDefault(k))];
    }

    /// <summary>
    /// Reads the state of one file's journey, or null when the product has no policy — which a
    /// session cannot normally reach, since a link is only issued for an eligible product, but can
    /// happen if an administrator disabled remote KYC while a customer was mid-upload.
    /// </summary>
    internal async Task<JourneyState?> ReadAsync(
        KycRemoteInvitation invitation, CancellationToken ct)
    {
        var productLineId = await db.KycFiles
            .IgnoreQueryFilters()
            .Where(f => f.TenantId == invitation.TenantId && f.Id == invitation.KycFileId)
            .Select(f => f.ProductLineId)
            .FirstOrDefaultAsync(ct);

        if (productLineId is not { } lineId) return null;

        var policy = await administration.GetKycRemotePolicyAsync(invitation.TenantId, lineId, ct);
        if (policy is null || !policy.Enabled) return null;

        var allowedTypes = policy.AllowedDocumentTypes
            .Select(ParseType)
            .Where(t => t is not null)
            .Select(t => t!.Value)
            .Distinct()
            .ToList();

        // The documents already on file for this JOURNEY. Scoped to the invitation's file and to
        // the uploads the customer made — a proof of address an agent had attached earlier counts,
        // because the requirement is "the file has one", not "the customer sent one".
        var uploadedKinds = await db.KycDocuments
            .IgnoreQueryFilters()
            .Where(d => d.TenantId == invitation.TenantId && d.KycFileId == invitation.KycFileId)
            .Select(d => d.Kind)
            .Distinct()
            .ToListAsync(ct);

        // KRM-B-10: on a complement link the kinds the agent asked to be RETAKEN must stop
        // counting as present, or the journey opens with nothing missing and the send button
        // resubmits the very image that was refused. Only those kinds, and only the ones uploaded
        // BEFORE this link existed — a photograph taken during this round counts normally, which
        // is how the customer makes progress.
        var retakeKinds = RetakeKinds(invitation);

        var supersededKinds = retakeKinds.Count == 0
            ? []
            : await db.KycDocuments
                .IgnoreQueryFilters()
                .Where(d => d.TenantId == invitation.TenantId
                         && d.KycFileId == invitation.KycFileId
                         && d.UploadedAt >= invitation.CreatedAt)
                .Select(d => d.Kind)
                .Distinct()
                .ToListAsync(ct);

        var uploaded = Enum.GetValues<KycDocumentKind>()
            .ToDictionary(
                k => k,
                k => retakeKinds.Contains(k)
                    ? supersededKinds.Contains(k)
                    : uploadedKinds.Contains(k));

        // Which type the customer chose, read off the FRONT they uploaded — the image the choice
        // was made for. Not stored on the invitation: a re-issued link would lose it, and it would
        // be one more field to keep in step with the documents it describes.
        //
        // The newest front wins, because retaking the photo is how a customer corrects a wrong
        // choice.
        var chosenType = await db.KycDocuments
            .IgnoreQueryFilters()
            .Where(d => d.TenantId == invitation.TenantId
                     && d.KycFileId == invitation.KycFileId
                     && d.Kind == KycDocumentKind.IdentityDocumentFront
                     && d.DeclaredDocumentType != null)
            .OrderByDescending(d => d.UploadedAt)
            .Select(d => d.DeclaredDocumentType)
            .FirstOrDefaultAsync(ct);

        // A complement link asks for exactly what the agent listed and nothing else — not even the
        // front and the selfie, which an initial link always collects. A customer told "your
        // utility bill is unreadable" must not be walked through photographing their identity card
        // again; the file already holds a reading of it.
        var required = retakeKinds.Count > 0
            ? [.. retakeKinds]
            : new List<KycDocumentKind>(AlwaysRequired);

        if (retakeKinds.Count == 0)
        {
            foreach (var raw in policy.RequiredDocuments)
            {
                if (ParseKind(raw) is { } kind && !required.Contains(kind))
                    required.Add(kind);
            }
        }

        // The back depends on the TYPE the customer chose, not on the product — so it only joins
        // the list once they have chosen, and never for a passport. Skipped entirely on a
        // complement: the scope is the agent's list, and adding a back they did not ask for would
        // make their request unsatisfiable.
        if (retakeKinds.Count == 0
            && chosenType is { } type && HasBack(type)
            && !required.Contains(KycDocumentKind.IdentityDocumentBack))
        {
            required.Add(KycDocumentKind.IdentityDocumentBack);
        }

        var maxRetries = await settings.GetIntAsync(
            invitation.TenantId, KycSettingKeys.RemoteMaxQualityRetries, ct);

        return new JourneyState(
            Policy: policy,
            AllowedDocumentTypes: allowedTypes,
            RequiredKinds: required,
            Uploaded: uploaded,
            ChosenType: chosenType,
            RetriesUsed: invitation.QualityRetries(),
            MaxRetries: maxRetries,
            HasConsented: invitation.HasConsented);
    }

    /// <summary>
    /// The kinds a complement link asks to be retaken, parsed. Empty for an initial link, and
    /// empty for a complement that named none — which the caller reads as "the product's own
    /// list", so one branch covers both.
    /// </summary>
    private static HashSet<KycDocumentKind> RetakeKinds(KycRemoteInvitation invitation)
    {
        if (invitation.Purpose != KycRemoteInvitationPurpose.Complement) return [];

        return [.. invitation.RequestedKinds()
            .Select(ParseKind)
            .Where(k => k is not null)
            .Select(k => k!.Value)];
    }

    private static KycDocumentType? ParseType(string? raw)
        => Enum.TryParse<KycDocumentType>(raw, ignoreCase: true, out var parsed) ? parsed : null;

    private static KycDocumentKind? ParseKind(string? raw)
        => Enum.TryParse<KycDocumentKind>(raw, ignoreCase: true, out var parsed) ? parsed : null;
}
