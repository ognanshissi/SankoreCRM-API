namespace Sankore.Modules.Leads.Features.LeadSources.ProviderDoc;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class GetProviderDocHandler(LeadsDbContext db)
    : IRequestHandler<GetProviderDocQuery, Result<ProviderDocResult>>
{
    public async Task<Result<ProviderDocResult>> Handle(
        GetProviderDocQuery query, CancellationToken ct)
    {
        var source = await db.LeadSourceConfigs
            .FirstOrDefaultAsync(s => s.Id == query.SourceId, ct);

        if (source is null)
            return Result.Fail<ProviderDocResult>("SOURCE_NOT_FOUND");

        if (source.Mode != IntegrationMode.ServerWebhook)
            return Result.Fail<ProviderDocResult>("PROVIDER_DOC_ONLY_FOR_WEBHOOK");

        var settings = source.Settings as ServerWebhookSettings;
        if (settings is null)
            return Result.Fail<ProviderDocResult>("NO_WEBHOOK_SETTINGS");

        var pdfBytes = ProviderDocPdfGenerator.Generate(source, settings);
        var fileName = $"webhook-integration-{source.Code.ToLowerInvariant()}.pdf";

        return Result.Ok(new ProviderDocResult(pdfBytes, fileName));
    }
}
