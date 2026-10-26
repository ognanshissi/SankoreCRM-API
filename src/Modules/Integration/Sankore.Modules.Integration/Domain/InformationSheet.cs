namespace Sankore.Modules.Integration.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// One version of an insurance product's information sheet — the insurer's PDF (insurance lot 2).
/// Never replaced in place: a delivery names the version the client received, and that version
/// must stay readable for as long as the proof is kept.
/// </summary>
public sealed class ProductInformationSheet : AggregateRoot
{
    public Guid Id { get; private set; }
    public Guid ProductId { get; private set; }
    public int Version { get; private set; }
    public string FileReference { get; private set; } = string.Empty;
    public string FileName { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = string.Empty;
    public long SizeBytes { get; private set; }

    /// <summary>SHA-256 of the file: what a delivery proves was handed over.</summary>
    public string Sha256 { get; private set; } = string.Empty;

    public DateTimeOffset UploadedAt { get; private set; }
    public Guid UploadedBy { get; private set; }

    private ProductInformationSheet() { }

    public static ProductInformationSheet Create(
        Guid tenantId, Guid productId, int version, string fileReference, string fileName,
        string contentType, long sizeBytes, string sha256, Guid uploadedBy, TimeProvider clock)
    {
        if (productId == Guid.Empty) throw new DomainException("ProductId is required.");
        if (version < 1) throw new DomainException("A sheet version starts at 1.");
        if (string.IsNullOrWhiteSpace(fileReference)) throw new DomainException("The stored file is required.");

        return new ProductInformationSheet
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ProductId = productId,
            Version = version,
            FileReference = fileReference,
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = sizeBytes,
            Sha256 = sha256,
            UploadedAt = clock.GetUtcNow(),
            UploadedBy = uploadedBy,
        };
    }
}

/// <summary>How the information sheet reached the client.</summary>
public enum InformationSheetChannel
{
    /// <summary>Printed and handed over at the counter.</summary>
    HandedOnPaper,

    /// <summary>Shown and read with the client on the agent's screen.</summary>
    ShownOnScreen,

    /// <summary>Sent by e-mail by the agent.</summary>
    SentByEmail,

    /// <summary>Made available in the client's own space.</summary>
    CustomerSpace,
}

/// <summary>
/// The proof that a client received a product's information sheet before signing (insurance lot
/// 2): which version (number and fingerprint), when, how, and the acknowledgement the client gave.
/// A holding on an insurance line cannot be signed without one, and the subscription must name the
/// product whose sheet was delivered. Kept as long as the rest of the subscription's evidence.
/// </summary>
public sealed class InformationSheetDelivery : AggregateRoot
{
    public Guid Id { get; private set; }
    public Guid HoldingId { get; private set; }
    public Guid CrmCustomerId { get; private set; }
    public Guid ProductId { get; private set; }
    public Guid SheetId { get; private set; }
    public int SheetVersion { get; private set; }
    public string SheetSha256 { get; private set; } = string.Empty;
    public InformationSheetChannel Channel { get; private set; }

    /// <summary>The client's acknowledgement: signed receipt number, e-mail reference, read confirmation…</summary>
    public string AcknowledgementReference { get; private set; } = string.Empty;

    public DateTimeOffset DeliveredAt { get; private set; }
    public Guid DeliveredBy { get; private set; }
    public DateTimeOffset? RetainUntil { get; private set; }

    private InformationSheetDelivery() { }

    public static InformationSheetDelivery Record(
        Guid tenantId, Guid holdingId, Guid crmCustomerId, ProductInformationSheet sheet,
        InformationSheetChannel channel, string acknowledgementReference, Guid deliveredBy,
        TimeProvider clock, DateTimeOffset? retainUntil)
    {
        ArgumentNullException.ThrowIfNull(sheet);

        if (string.IsNullOrWhiteSpace(acknowledgementReference))
            throw new DomainException("A delivery is proven by the client's acknowledgement: its reference is required.");

        return new InformationSheetDelivery
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            HoldingId = holdingId,
            CrmCustomerId = crmCustomerId,
            ProductId = sheet.ProductId,
            SheetId = sheet.Id,
            SheetVersion = sheet.Version,
            SheetSha256 = sheet.Sha256,
            Channel = channel,
            AcknowledgementReference = acknowledgementReference.Trim(),
            DeliveredAt = clock.GetUtcNow(),
            DeliveredBy = deliveredBy,
            RetainUntil = retainUntil,
        };
    }
}
