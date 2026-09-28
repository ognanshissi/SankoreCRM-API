namespace Sankore.Modules.Customers.Features.Clients.Shared;

using System.Globalization;
using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Infrastructure.Crypto;

/// <summary>
/// Turns the <c>Client</c> aggregate into the DTOs the HTTP layer returns.
///
/// This type is the single place where a protected column is decrypted on a READ
/// path, and it never lets the clear value escape: every decrypt is immediately
/// followed by a <see cref="SensitiveValueMasker"/> call in the same expression, so
/// there is no branch through which an un-masked value can reach a response body.
/// The one endpoint that legitimately returns clear text (<c>reveal</c>) decrypts on
/// its own and does not go through here.
///
/// A decrypt that fails (key rotated, row written by another environment) must not
/// take the whole detail screen down: the field degrades to <c>null</c>, which the UI
/// renders as "unavailable".
/// </summary>
public static class ClientDtoMapper
{
    /// <summary>How many status transitions the detail DTO carries (most recent first).</summary>
    public const int StatusHistoryPageSize = 5;

    /// <summary>
    /// Full detail projection. <paramref name="mergedInto"/> is resolved by the caller
    /// (it needs a second row) and is only non-null for a <c>Merged</c> client.
    /// </summary>
    public static ClientDetailDto ToDetail(
        Client client,
        IFieldEncryptor encryptor,
        MergedIntoDto? mergedInto)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(encryptor);

        var contactPoints = client.ContactPoints
            .Where(cp => cp.IsActive)
            .OrderByDescending(cp => cp.IsPrimary)
            .ThenBy(cp => cp.Type)
            .ThenBy(cp => cp.ValidFrom)
            .Select(cp => ToDto(cp, encryptor))
            .ToList();

        var statusHistory = client.StatusHistory
            .OrderByDescending(h => h.OccurredAt)
            .Take(StatusHistoryPageSize)
            .Select(ToDto)
            .ToList();

        return new ClientDetailDto(
            Id: client.Id,
            ClientNumber: client.ClientNumber,
            ClientType: client.Type.ToString(),
            Status: client.Status.ToString(),
            DisplayName: client.DisplayName,

            FirstName: client.FirstName,
            LastName: client.LastName,
            MaidenName: client.MaidenName,
            Gender: client.Gender?.ToString(),
            BirthPlace: client.BirthPlace,
            Nationality: client.Nationality,
            MaritalStatus: client.MaritalStatus?.ToString(),
            FatherName: client.FatherName,
            MotherName: client.MotherName,
            Profession: client.Profession,
            Employer: client.Employer,
            PreferredLanguage: client.PreferredLanguage,
            DependentsCount: client.DependentsCount,

            DateOfBirthMasked: MaskEncryptedDate(client.EncryptedDateOfBirth, encryptor),
            DeclaredIncomeMasked: MaskEncryptedAmount(client.EncryptedDeclaredIncome, encryptor),
            DeclaredIncomeCurrency: client.DeclaredIncomeCurrency,
            IdentityDocumentType: client.IdentityDocumentType?.ToString(),
            IdentityDocumentNumberMasked:
                MaskEncryptedDocument(client.EncryptedIdentityDocumentNumber, encryptor),
            IdentityDocumentIssuedOn: client.IdentityDocumentIssuedOn,
            IdentityDocumentExpiresOn: client.IdentityDocumentExpiresOn,

            LegalName: client.LegalName,
            LegalFormCode: client.LegalFormCode,
            RegistrationNumberMasked:
                MaskEncryptedDocument(client.EncryptedRegistrationNumber, encryptor),
            TaxIdNumberMasked: MaskEncryptedDocument(client.EncryptedTaxIdNumber, encryptor),
            IncorporationDate: client.IncorporationDate,

            AgencyId: client.AgencyId,
            AgencyCode: client.AgencyCode,
            AdvisorUserId: client.AdvisorUserId,
            KycStatus: client.KycStatus.ToString(),
            KycRejectionReason: client.KycRejectionReason,
            RiskLevel: client.RiskLevel.ToString(),
            SegmentCode: client.SegmentCode,
            LoyaltyScore: client.LoyaltyScore,
            LoyaltyScoreProvisional: client.LoyaltyScoreProvisional,

            SourceLeadId: client.SourceLeadId,
            MergedInto: mergedInto,
            IsAnonymized: client.IsAnonymized,
            ArchivedAt: client.ArchivedAt,
            CreatedAt: client.CreatedAt,
            UpdatedAt: client.UpdatedAt,
            Version: client.Version,

            ContactPoints: contactPoints,
            StatusHistory: statusHistory);
    }

    /// <summary>
    /// Masked projection of one contact point. Also used by the ContactPoints zone, so
    /// a phone, an e-mail and an address are masked identically everywhere.
    /// </summary>
    public static ClientContactPointDto ToDto(ClientContactPoint contactPoint, IFieldEncryptor encryptor)
    {
        ArgumentNullException.ThrowIfNull(contactPoint);
        ArgumentNullException.ThrowIfNull(encryptor);

        return new ClientContactPointDto(
            Id: contactPoint.Id,
            Type: contactPoint.Type.ToString(),
            ValueMasked: MaskEncryptedContactValue(contactPoint.Type, contactPoint.EncryptedValue, encryptor),
            Label: contactPoint.Label,
            IsPrimary: contactPoint.IsPrimary,
            ValidFrom: contactPoint.ValidFrom,
            ValidTo: contactPoint.ValidTo,
            IsActive: contactPoint.IsActive);
    }

    /// <summary>One status transition, no protected data involved.</summary>
    public static ClientStatusHistorySummaryDto ToDto(ClientStatusHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);

        return new ClientStatusHistorySummaryDto(
            OldStatus: history.OldStatus?.ToString(),
            NewStatus: history.NewStatus.ToString(),
            Reason: history.Reason,
            ActorUserId: history.ActorUserId,
            OccurredAt: history.OccurredAt);
    }

    /// <summary>
    /// Decrypts a contact point and masks it according to its type — the shape of the
    /// mask matters: an operator recognises a phone by its last two digits and an
    /// e-mail by its first letter and TLD.
    /// </summary>
    public static string? MaskEncryptedContactValue(
        ContactPointType type, string? encryptedValue, IFieldEncryptor encryptor)
    {
        ArgumentNullException.ThrowIfNull(encryptor);

        var clear = TryDecrypt(encryptedValue, encryptor);
        if (clear is null) return null;

        return type switch
        {
            ContactPointType.Phone => SensitiveValueMasker.MaskPhone(clear),
            ContactPointType.Email => SensitiveValueMasker.MaskEmail(clear),
            ContactPointType.Address => SensitiveValueMasker.MaskGeneric(clear),
            _ => SensitiveValueMasker.MaskGeneric(clear)
        };
    }

    /// <summary>Decrypts a document / registration / tax number and masks it.</summary>
    public static string? MaskEncryptedDocument(string? encryptedValue, IFieldEncryptor encryptor)
    {
        ArgumentNullException.ThrowIfNull(encryptor);

        var clear = TryDecrypt(encryptedValue, encryptor);
        return clear is null ? null : SensitiveValueMasker.MaskDocument(clear);
    }

    /// <summary>
    /// Decrypts an ISO date ("yyyy-MM-dd") and masks everything but the year — a year
    /// alone is what the age check and the KYC file need to be plausible, and it is
    /// not enough to impersonate anybody.
    /// </summary>
    public static string? MaskEncryptedDate(string? encryptedValue, IFieldEncryptor encryptor)
    {
        ArgumentNullException.ThrowIfNull(encryptor);

        var clear = TryDecrypt(encryptedValue, encryptor);
        if (clear is null) return null;

        return DateOnly.TryParse(clear, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? SensitiveValueMasker.MaskDate(parsed)
            : SensitiveValueMasker.MaskGeneric(clear);
    }

    /// <summary>Decrypts a declared income and masks it (order of magnitude hidden too).</summary>
    public static string? MaskEncryptedAmount(string? encryptedValue, IFieldEncryptor encryptor)
    {
        ArgumentNullException.ThrowIfNull(encryptor);

        var clear = TryDecrypt(encryptedValue, encryptor);
        return clear is null ? null : SensitiveValueMasker.MaskGeneric(clear);
    }

    /// <summary>
    /// Decrypt that degrades instead of throwing. A single unreadable row (key rotated
    /// mid-migration, data copied between environments) must not 500 the whole detail
    /// screen — the field simply comes back null.
    /// </summary>
    private static string? TryDecrypt(string? encryptedValue, IFieldEncryptor encryptor)
    {
        if (string.IsNullOrWhiteSpace(encryptedValue)) return null;

        try
        {
            var clear = encryptor.Decrypt(encryptedValue);
            return string.IsNullOrWhiteSpace(clear) ? null : clear;
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException
                                      or System.Security.Cryptography.CryptographicException
                                      or ArgumentException)
        {
            return null;
        }
    }
}
