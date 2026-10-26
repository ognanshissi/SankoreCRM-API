namespace Sankore.Modules.Administration.Domain;

using Sankore.Shared.Kernel;

/// <summary>The institution's legal status as an insurance distributor.</summary>
public enum InsuranceDistributorStatus
{
    /// <summary>Courtier : the art. 540 mention appears on every document handed to a client.</summary>
    Broker,

    /// <summary>Agent général d'un assureur.</summary>
    GeneralAgent,

    /// <summary>Banque ou IMF partenaire d'un assureur (bancassurance).</summary>
    BancassurancePartner,
}

/// <summary>The pieces a distributor holds to be allowed to distribute.</summary>
public enum DistributorDocumentKind
{
    /// <summary>Agrément (courtier) or mandate of general agency.</summary>
    Approval,

    /// <summary>Garantie financière.</summary>
    FinancialGuarantee,

    /// <summary>Attestation d'assurance de responsabilité civile professionnelle.</summary>
    ProfessionalLiability,
}

/// <summary>
/// The institution as an insurance DISTRIBUTOR (tenant level, insurance lot 2): its status, the
/// legal mention its documents carry, the annual SMIG the 60-day payment derogation is computed
/// from, and the pieces that allow it to distribute.
///
/// <para>
/// Distinct from <see cref="DistributionCredential"/>, which is one AGENT's professional card. The
/// two are checked together before a subscription: the person must be carded AND the institution
/// in good standing.
/// </para>
/// </summary>
public sealed class InsuranceDistributorProfile : ITenant
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }

    public InsuranceDistributorStatus Status { get; private set; }

    /// <summary>ORIAS-like registration number, as printed on the documents.</summary>
    public string? RegistrationNumber { get; private set; }

    /// <summary>
    /// The mention the art. 540 requires on every document a broker hands to a client. Required for
    /// a broker; free for the others, who may still print one.
    /// </summary>
    public string? LegalMention { get; private set; }

    /// <summary>
    /// The annual SMIG (salaire minimum interprofessionnel garanti), in the institution's currency.
    /// A premium above 80 times this amount may be paid within 60 days of the policy's issue.
    /// Null: no derogation is possible.
    /// </summary>
    public decimal? AnnualSmig { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }
    public Guid UpdatedBy { get; private set; }

    private InsuranceDistributorProfile() { } // EF Core

    public static InsuranceDistributorProfile Create(Guid tenantId, Guid by, DateTimeOffset now)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Status = InsuranceDistributorStatus.BancassurancePartner,
            UpdatedAt = now,
            UpdatedBy = by,
        };

    public Result Update(
        InsuranceDistributorStatus status,
        string? registrationNumber,
        string? legalMention,
        decimal? annualSmig,
        Guid by,
        DateTimeOffset now)
    {
        if (status == InsuranceDistributorStatus.Broker && string.IsNullOrWhiteSpace(legalMention))
            return Result.Fail(InsuranceDistributionErrors.LegalMentionRequired);

        if (annualSmig is <= 0)
            return Result.Fail(InsuranceDistributionErrors.SmigInvalid);

        Status = status;
        RegistrationNumber = string.IsNullOrWhiteSpace(registrationNumber) ? null : registrationNumber.Trim();
        LegalMention = string.IsNullOrWhiteSpace(legalMention) ? null : legalMention.Trim();
        AnnualSmig = annualSmig;
        UpdatedAt = now;
        UpdatedBy = by;
        return Result.Ok();
    }

    /// <summary>
    /// The pieces a status must hold. A broker answers for the premiums it handles and needs all
    /// three; an agent or a bank partner works under the insurer's own guarantee.
    /// </summary>
    public static IReadOnlyList<DistributorDocumentKind> RequiredKinds(InsuranceDistributorStatus status)
        => status == InsuranceDistributorStatus.Broker
            ? [DistributorDocumentKind.Approval, DistributorDocumentKind.FinancialGuarantee, DistributorDocumentKind.ProfessionalLiability]
            : [DistributorDocumentKind.Approval, DistributorDocumentKind.ProfessionalLiability];
}

/// <summary>
/// One piece of the distributor, with its expiry. A renewal is a NEW row that supersedes the
/// previous one of the same kind: the old one stays, because it is what covered the contracts
/// signed while it was in force.
/// </summary>
public sealed class DistributorDocument : ITenant
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }

    public DistributorDocumentKind Kind { get; private set; }

    /// <summary>Number of the agrément, of the guarantee, of the RC policy.</summary>
    public string Reference { get; private set; } = default!;

    /// <summary>Who issued it: the supervisory authority, the guarantor, the RC insurer.</summary>
    public string? Issuer { get; private set; }

    public DateOnly? IssuedOn { get; private set; }

    /// <summary>Null: no expiry (an agrément without a term).</summary>
    public DateOnly? ExpiresOn { get; private set; }

    /// <summary>The scanned piece in the file store, when one was attached.</summary>
    public string? FileReference { get; private set; }
    public string? FileName { get; private set; }
    public string? ContentType { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }

    /// <summary>Set when a newer piece of the same kind replaced it.</summary>
    public DateTimeOffset? SupersededAt { get; private set; }

    public bool IsCurrent => SupersededAt is null;

    private DistributorDocument() { } // EF Core

    public static DistributorDocument Record(
        Guid tenantId,
        DistributorDocumentKind kind,
        string reference,
        string? issuer,
        DateOnly? issuedOn,
        DateOnly? expiresOn,
        string? fileReference,
        string? fileName,
        string? contentType,
        Guid by,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reference))
            throw new DomainException(InsuranceDistributionErrors.ReferenceRequired, "DistributorDocument.Reference.Required");

        if (issuedOn is { } issued && expiresOn is { } expires && expires < issued)
            throw new DomainException(InsuranceDistributionErrors.ExpiryBeforeIssue, "DistributorDocument.Dates.Invalid");

        return new DistributorDocument
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Kind = kind,
            Reference = reference.Trim(),
            Issuer = string.IsNullOrWhiteSpace(issuer) ? null : issuer.Trim(),
            IssuedOn = issuedOn,
            ExpiresOn = expiresOn,
            FileReference = fileReference,
            FileName = fileName,
            ContentType = contentType,
            CreatedAt = now,
            CreatedBy = by,
        };
    }

    public void Supersede(DateTimeOffset now) => SupersededAt ??= now;

    public bool IsValidOn(DateOnly day) => ExpiresOn is not { } expires || expires >= day;
}

public static class InsuranceDistributionErrors
{
    public const string LegalMentionRequired = "INSURANCE_DISTRIBUTOR_LEGAL_MENTION_REQUIRED";
    public const string SmigInvalid = "INSURANCE_DISTRIBUTOR_SMIG_INVALID";
    public const string DocumentNotFound = "INSURANCE_DISTRIBUTOR_DOCUMENT_NOT_FOUND";
    public const string FileTooLarge = "INSURANCE_DISTRIBUTOR_DOCUMENT_TOO_LARGE";
    public const string FileTypeNotAllowed = "INSURANCE_DISTRIBUTOR_DOCUMENT_TYPE_NOT_ALLOWED";
    public const string ReferenceRequired = "INSURANCE_DISTRIBUTOR_DOCUMENT_REFERENCE_REQUIRED";
    public const string ExpiryBeforeIssue = "INSURANCE_DISTRIBUTOR_DOCUMENT_EXPIRY_BEFORE_ISSUE";
}
