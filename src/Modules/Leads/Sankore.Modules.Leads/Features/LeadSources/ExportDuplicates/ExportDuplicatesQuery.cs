namespace Sankore.Modules.Leads.Features.LeadSources.ExportDuplicates;

using MediatR;
using Sankore.Shared.Kernel;

internal sealed record ExportDuplicatesQuery(Guid SourceId)
    : IRequest<Result<ExportDuplicatesResult>>;

public sealed record ExportDuplicatesResult(byte[] CsvBytes, string FileName);
