namespace Sankore.Modules.Customers.Domain;

using Sankore.Shared.Kernel;

/// <summary>What an agent filed on the client record: the three kinds the product owner named.</summary>
public enum ClientDocumentKind
{
    /// <summary>A letter, sent or received.</summary>
    Correspondence,

    /// <summary>A supporting document: a payslip, a certificate, a bank statement.</summary>
    Proof,

    Other,
}

/// <summary>
/// A file an agent deposited straight on the client record — one that belongs to no KYC file,
/// contract or claim (product owner, 2026-10-26). The bytes are in the encrypted client-document
/// store; this row says whose, what, which way it went and who put it.
/// </summary>
/// <remarks>
/// Removal is LOGICAL and traced (who, when, why): the row stays, the file stays in the store, and
/// the list no longer shows it. A document an institution exchanged with a client is evidence, and
/// a click must not be able to make it as though it never existed.
/// </remarks>
public sealed class ClientDocument : ITenant
{
    public const int TitleMaxLength = 200;
    public const int ReasonMaxLength = 500;

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid ClientId { get; private set; }
    public ClientDocumentKind Kind { get; private set; }

    /// <summary>What the agent called it — « Courrier de relance du 12/10 ».</summary>
    public string Title { get; private set; } = default!;

    /// <summary>Received from the client, or handed to them.</summary>
    public CustomerDocumentDirection Direction { get; private set; }

    /// <summary>The document's own date, which is not the day it was filed here.</summary>
    public DateOnly DocumentDate { get; private set; }

    public string FileName { get; private set; } = default!;
    public string ContentType { get; private set; } = default!;
    public long SizeBytes { get; private set; }
    public string Sha256 { get; private set; } = default!;
    public string StorageRef { get; private set; } = default!;
    public Guid UploadedBy { get; private set; }
    public DateTimeOffset UploadedAt { get; private set; }

    public DateTimeOffset? RemovedAt { get; private set; }
    public Guid? RemovedBy { get; private set; }
    public string? RemovalReason { get; private set; }

    private ClientDocument() { }

    public static ClientDocument Record(
        Guid tenantId, Guid clientId, ClientDocumentKind kind, string title, CustomerDocumentDirection direction,
        DateOnly documentDate, string fileName, string contentType, long sizeBytes, string sha256,
        string storageRef, Guid uploadedBy, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (clientId == Guid.Empty) throw new DomainException("ClientId is required.");
        if (string.IsNullOrWhiteSpace(title)) throw new DomainException("A client document needs a title.");

        return new ClientDocument
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = clientId,
            Kind = kind,
            Title = title.Trim()[..Math.Min(title.Trim().Length, TitleMaxLength)],
            Direction = direction,
            DocumentDate = documentDate,
            FileName = string.IsNullOrWhiteSpace(fileName) ? "document" : Path.GetFileName(fileName.Trim()),
            ContentType = contentType,
            SizeBytes = sizeBytes,
            Sha256 = sha256,
            StorageRef = storageRef,
            UploadedBy = uploadedBy,
            UploadedAt = now,
        };
    }

    public bool IsRemoved => RemovedAt is not null;

    public Result Remove(Guid actorId, string reason, DateTimeOffset now)
    {
        if (IsRemoved) return Result.Fail(CustomerErrors.ClientDocumentNotFound);
        if (string.IsNullOrWhiteSpace(reason)) return Result.Fail(CustomerErrors.ClientDocumentRemovalReasonRequired);

        RemovedAt = now;
        RemovedBy = actorId;
        RemovalReason = reason.Trim()[..Math.Min(reason.Trim().Length, ReasonMaxLength)];
        return Result.Ok();
    }
}
