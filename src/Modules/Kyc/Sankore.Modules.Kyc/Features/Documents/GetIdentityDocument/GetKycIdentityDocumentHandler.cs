namespace Sankore.Modules.Kyc.Features.Documents.GetIdentityDocument;

using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Kernel;

/// <summary>
/// Reads the latest identity document of a file and masks its number.
///
/// <para>
/// This endpoint is NOT audited, deliberately, and that is a consequence of the masking rather than
/// an omission: <c>kyc_document_access_logs</c> exists to record who saw a document NUMBER or its
/// image, and a masked number is not one. Audit every OCR panel opening and the table stops being
/// the short, meaningful list a regulator can read — the reveal slice and the image stream remain
/// audited, which is where the number actually changes hands.
/// </para>
/// </summary>
internal sealed class GetKycIdentityDocumentHandler(
    KycDbContext db,
    [FromKeyedServices(KycFieldProtection.Key)] IFieldEncryptor encryptor,
    TimeProvider clock,
    ILogger<GetKycIdentityDocumentHandler> logger)
    : IRequestHandler<GetKycIdentityDocumentQuery, Result<KycIdentityDocumentDto>>
{
    public async Task<Result<KycIdentityDocumentDto>> Handle(
        GetKycIdentityDocumentQuery query, CancellationToken ct)
    {
        var fileExists = await db.KycFiles.AnyAsync(f => f.Id == query.KycFileId, ct);
        if (!fileExists)
            return Result.Fail<KycIdentityDocumentDto>(KycErrors.FileNotFound);

        // Latest: a re-verification adds a row rather than editing one, so the newest reading is
        // the one the file's current score was computed from.
        var document = await db.KycIdentityDocuments
            .Where(d => d.KycFileId == query.KycFileId)
            .OrderByDescending(d => d.CreatedAt)
            .FirstOrDefaultAsync(ct);

        // KYC_IDENTITY_DOCUMENT_NOT_FOUND, the same code CorrectKycFieldHandler already answers for
        // this exact state: the file exists and has no reading yet. Distinct from
        // KYC_DOCUMENT_NOT_FOUND, which is the image stream's "that storage ref is not yours".
        if (document is null)
            return Result.Fail<KycIdentityDocumentDto>(KycErrors.IdentityDocumentNotFound);

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        return Result.Ok(new KycIdentityDocumentDto(
            KycFileId: query.KycFileId,
            DocType: document.DocType,
            MaskedNumber: MaskNumber(document),
            IssuingCountry: document.IssuingCountry,
            ExpiryDate: document.ExpiryDate,
            IsExpired: document.IsExpiredOn(today),
            Fields: Deserialize<Dictionary<string, string>>(document.OcrFieldsJson, "ocr_fields_json") ?? [],
            FieldConfidences: Deserialize<Dictionary<string, double>>(
                document.OcrFieldConfidencesJson, "ocr_field_confidences_json") ?? [],
            Mrz: ReadMrz(document.MrzDataJson),
            StorageRef: document.StorageRef,
            ServiceVersion: document.ServiceVersion,
            ReadAt: document.CreatedAt));
    }

    /// <summary>
    /// Decrypts, then masks. The clear value exists for the lifetime of this method and reaches no
    /// response, no log and no exception message — a failure to decrypt is reported as the column
    /// name and nothing else, because the only thing worth saying is which key is wrong.
    /// </summary>
    private string? MaskNumber(KycIdentityDocument document)
    {
        try
        {
            return SensitiveValueMasker.MaskDocument(encryptor.Decrypt(document.EncryptedNumber));
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            // Not a failure of the panel: the fields, the confidences and the zone are all still
            // readable and are what the agent came for. A wrong Kyc:FieldEncryptionKey is an
            // operations incident, logged as one.
            logger.LogError(ex,
                "Could not decrypt the number of identity document {DocumentId}; the panel shows no number. "
                + "Check Kyc:FieldEncryptionKey.", document.Id);

            return null;
        }
    }

    private KycMrzDto? ReadMrz(string? mrzDataJson)
    {
        var mrz = Deserialize<StoredMrz>(mrzDataJson, "mrz_data_json");
        if (mrz is null) return null;

        return new KycMrzDto(
            // See KycMrzDto: the raw line is never stored.
            RawLine: null,
            ChecksumValid: mrz.ChecksumValid,
            Fields: mrz.Fields ?? []);
    }

    /// <summary>
    /// A malformed blob yields null and a warning, never a 500: these columns hold whatever an
    /// external service sent, and one unreadable section must not hide the readable ones.
    /// </summary>
    private T? Deserialize<T>(string? json, string column) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Could not read {Column} of a KYC identity document; the section is omitted", column);
            return null;
        }
    }

    /// <summary>
    /// Case-insensitive on purpose: the MRZ blob is written by <c>RunKycVerificationHandler</c> with
    /// an anonymous type, so its keys are camelCase, while the records here are PascalCase.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The shape RunKycVerificationHandler serialises into <c>mrz_data_json</c>.</summary>
    private sealed record StoredMrz(bool ChecksumValid, Dictionary<string, string>? Fields);
}
