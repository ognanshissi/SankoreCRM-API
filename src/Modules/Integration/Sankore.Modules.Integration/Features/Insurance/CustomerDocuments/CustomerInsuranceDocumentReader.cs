namespace Sankore.Modules.Integration.Features.Insurance.CustomerDocuments;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Kernel;

/// <summary>
/// Everything this module holds that a client gave or received, for the client record's
/// « Documents » tab: information sheets handed over, the insurer's policy documents, claim pieces
/// and consent proofs. Metadata only; each file opens through its own endpoint, which keeps its
/// permission.
/// </summary>
/// <remarks>
/// Tenant-scoped by the context's global filter. The questionnaire answers are not listed: they
/// are answers, not documents, and the health ones sit behind their own door.
/// </remarks>
internal static class CustomerInsuranceDocumentReader
{
    public static async Task<IReadOnlyList<CustomerDocumentEntry>> ReadAsync(
        IntegrationDbContext db, Guid crmCustomerId, CancellationToken ct)
    {
        var entries = new List<CustomerDocumentEntry>();

        var deliveries = await db.InformationSheetDeliveries
            .Where(d => d.CrmCustomerId == crmCustomerId)
            .Join(db.InformationSheets, d => d.SheetId, s => s.Id, (d, s) => new { Delivery = d, Sheet = s })
            .ToListAsync(ct);

        entries.AddRange(deliveries.Select(x => new CustomerDocumentEntry(
            Key: $"information-sheet:{x.Delivery.Id}",
            Source: "Insurance",
            Direction: CustomerDocumentDirection.Delivered,
            Kind: "InformationSheet",
            FileName: x.Sheet.FileName,
            ContentType: x.Sheet.ContentType,
            SizeBytes: x.Sheet.SizeBytes,
            At: x.Delivery.DeliveredAt,
            Status: "Delivered",
            Detail: x.Delivery.Channel.ToString(),
            OriginType: "Holding",
            OriginId: x.Delivery.HoldingId,
            OriginLabel: $"Version {x.Delivery.SheetVersion}",
            ContentPath: $"integration/insurance/information-sheets/{x.Delivery.SheetId}/content",
            OpenPermissions: [Permissions.CanViewInsurancePolicy.Code])));

        var policyDocuments = await db.PolicyDocuments
            .Where(d => d.CrmCustomerId == crmCustomerId)
            .Join(db.Policies, d => d.PolicyId, p => p.Id, (d, p) => new { Document = d, p.PolicyNumber })
            .ToListAsync(ct);

        entries.AddRange(policyDocuments.Select(x => new CustomerDocumentEntry(
            Key: $"policy-document:{x.Document.Id}",
            Source: "Insurance",
            Direction: CustomerDocumentDirection.Delivered,
            Kind: $"PolicyDocument.{x.Document.Kind}",
            FileName: x.Document.FileName,
            ContentType: x.Document.ContentType,
            SizeBytes: null,
            // The date that matters is the handing over; until then, the receipt from the insurer.
            At: x.Document.SentToClientAt ?? x.Document.UploadedAt,
            Status: x.Document.SentToClientAt is null ? "NotSent" : "Delivered",
            Detail: x.Document.SentChannel?.ToString(),
            OriginType: "Policy",
            OriginId: x.Document.PolicyId,
            OriginLabel: x.PolicyNumber,
            ContentPath: $"integration/insurance/policy-documents/{x.Document.Id}/content",
            OpenPermissions: [Permissions.CanViewInsurancePolicy.Code])));

        var claimPieces = await db.ClaimDocuments
            .Join(db.Claims.Where(c => c.CrmCustomerId == crmCustomerId), d => d.ClaimId, c => c.Id, (d, c) => d)
            .ToListAsync(ct);

        entries.AddRange(claimPieces.Select(d => new CustomerDocumentEntry(
            Key: $"claim-piece:{d.Id}",
            Source: "Insurance",
            Direction: CustomerDocumentDirection.Collected,
            Kind: "ClaimPiece",
            FileName: d.FileName,
            ContentType: d.ContentType,
            SizeBytes: d.SizeBytes,
            At: d.UploadedAt,
            Status: d.ScanStatus.ToString(),
            Detail: d.DocumentKind,
            OriginType: "Claim",
            OriginId: d.ClaimId,
            OriginLabel: "Sinistre",
            // Only a piece the antivirus passed may be read back: the endpoint answers 409 for the
            // rest, so the line does not offer to open it.
            ContentPath: d.ScanStatus == DocumentScanStatus.Clean
                ? $"integration/insurance/claims/{d.ClaimId}/documents/{d.Id}/content"
                : null,
            OpenPermissions: [Permissions.CanViewInsuranceClaim.Code])));

        var consents = await db.ConsentProofs
            .Where(c => c.CrmCustomerId == crmCustomerId && c.PurgedAt == null)
            .Join(db.InsuranceSubscriptions, c => c.SubscriptionId, s => s.Id, (c, s) => new { Consent = c, s.CrmHoldingId })
            .ToListAsync(ct);

        entries.AddRange(consents.Select(x => new CustomerDocumentEntry(
            Key: $"consent:{x.Consent.Id}",
            Source: "Insurance",
            Direction: CustomerDocumentDirection.Collected,
            Kind: "ConsentProof",
            FileName: null,
            ContentType: x.Consent.ContentType,
            SizeBytes: null,
            At: x.Consent.ConsentedAt,
            Status: null,
            Detail: x.Consent.Channel.ToString(),
            OriginType: "Holding",
            // A proof captured before subscriptions carried their holding links to its subscription.
            OriginId: x.CrmHoldingId ?? x.Consent.SubscriptionId,
            OriginLabel: $"Mentions v{x.Consent.ConsentTextVersion}",
            // Read through the subscription's evidence journal, on the contract page.
            ContentPath: null,
            OpenPermissions: [Permissions.CanViewInsurancePolicy.Code])));

        return [.. entries.OrderByDescending(e => e.At)];
    }
}
