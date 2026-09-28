namespace Sankore.Modules.Customers.Features.Compliance.Export.RequestClientExport;

using MediatR;
using Sankore.Modules.Customers.Features.Clients.SearchClients;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Queues an asynchronous export of a client search result (US-M01-BE-30).
/// <para>
/// <see cref="ICommand"/> is what makes this slice auditable, and that is the point of the US:
/// an export is the single largest personal-data egress the module offers, so "who exported
/// what, when" must be recorded — the payload carries <see cref="Filters"/> verbatim, which is
/// exactly the "what".
/// </para>
/// <para>
/// The filters are therefore NOT marked <c>[SensitiveData]</c>, even though a phone or document
/// filter is itself personal data: an audit entry saying only "an export happened" would answer
/// none of the questions an audit is for. The trade-off is deliberate — the audit trail is a
/// restricted surface (<c>audit:read</c>), the CSV is not.
/// </para>
/// </summary>
public sealed record RequestClientExportCommand(SearchClientsQuery Filters)
    : IRequest<Result<RequestClientExportResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "ClientExportJob";

    /// <summary>Null: the export id is assigned by the handler, not known at dispatch time.</summary>
    public string? ResourceId => null;
}

/// <summary>
/// Accepted-for-processing acknowledgement. No download URL yet — the file does not exist until
/// <see cref="GenerateClientExportJob"/> has run; the caller polls
/// <c>GET clients/exports/{exportId}</c>.
/// </summary>
public sealed record RequestClientExportResult(
    Guid ExportId,
    string Status,
    DateTimeOffset RequestedAt,
    DateTimeOffset ExpiresAt);
