namespace Sankore.Modules.Leads.Features.LeadSources.ProviderDoc;

using MediatR;
using Sankore.Shared.Kernel;

internal sealed record GetProviderDocQuery(Guid SourceId)
    : IRequest<Result<ProviderDocResult>>;

public sealed record ProviderDocResult(byte[] PdfBytes, string FileName);
