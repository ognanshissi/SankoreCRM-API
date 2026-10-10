namespace Sankore.Modules.Integration.Features.Mappings.ExportMappings;

using MediatR;
using Sankore.Modules.Integration.Domain;
using Sankore.Shared.Kernel;

/// <summary>
/// A query, not an <c>ICommand</c>: a mapping table holds no personal data — codes and operator
/// labels — so there is nothing here to justify an audit row on every read, unlike M01's client
/// export.
/// </summary>
internal sealed record ExportMappingsQuery(Guid ConnectionId, MappingDomain Domain)
    : IRequest<Result<MappingExportFile>>;

/// <summary>The rendered file, ready for <c>Results.File</c>.</summary>
internal sealed record MappingExportFile(byte[] Content, string FileName, string ContentType);
