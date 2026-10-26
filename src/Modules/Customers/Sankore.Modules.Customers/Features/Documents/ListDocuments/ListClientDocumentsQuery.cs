namespace Sankore.Modules.Customers.Features.Documents.ListDocuments;

using MediatR;
using Sankore.Modules.Customers.Features.Clients.Shared;
using Sankore.Shared.Kernel;

/// <summary>
/// Every document the client gave or received, whatever module keeps it (product owner,
/// 2026-10-26): KYC images, insurance documents, project and task attachments, WhatsApp media, the
/// files filed straight on the client record, and the e-mails and SMS the client was sent.
/// </summary>
public sealed record ListClientDocumentsQuery(Guid ClientId)
    : IClientReadRequest, IRequest<Result<ClientDocumentsDto>>;

/// <summary>One line of the client's documents.</summary>
/// <param name="Direction"><c>Collected</c> from the client or <c>Delivered</c> to them.</param>
/// <param name="CanOpen">
/// Whether this caller holds the permission the source's endpoint requires. A line the caller may
/// not open stays listed (product owner, 2026-10-26), but without its file name and its path —
/// a file name often carries the person's name or an identity number.
/// </param>
/// <param name="ContentPath">The API route (after <c>api/v1/</c>) that streams it; null when it cannot be opened.</param>
public sealed record ClientDocumentDto(
    string Key,
    string Source,
    string Direction,
    string Kind,
    string? FileName,
    string? ContentType,
    long? SizeBytes,
    DateTimeOffset At,
    string? Status,
    string? Detail,
    string OriginType,
    Guid OriginId,
    string? OriginLabel,
    string? ContentPath,
    bool CanOpen,
    /// <summary>A document filed on the client record, and the caller may update the client.</summary>
    bool CanRemove = false);

/// <param name="UnavailableSources">
/// <c>Kyc</c>, <c>Insurance</c>, <c>Leads</c> and/or <c>Messages</c> when that module could not be read: the list
/// is then partial and the screen says so.
/// </param>
public sealed record ClientDocumentsDto(
    IReadOnlyList<ClientDocumentDto> Items,
    IReadOnlyList<string> UnavailableSources);
