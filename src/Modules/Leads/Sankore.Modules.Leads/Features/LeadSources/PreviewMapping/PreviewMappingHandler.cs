namespace Sankore.Modules.Leads.Features.LeadSources.PreviewMapping;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.LeadSources.Mapping;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class PreviewMappingHandler(LeadsDbContext db)
    : IRequestHandler<PreviewMappingQuery, Result<PreviewMappingResult>>
{
    public async Task<Result<PreviewMappingResult>> Handle(
        PreviewMappingQuery query, CancellationToken ct)
    {
        var source = await db.LeadSourceConfigs
            .FirstOrDefaultAsync(s => s.Id == query.SourceId, ct);

        if (source is null)
            return Result.Fail<PreviewMappingResult>("SOURCE_NOT_FOUND");

        var fieldMapping = source.Settings switch
        {
            ServerWebhookSettings wh => wh.FieldMapping,
            ScheduledPullSettings pull => pull.FieldMapping,
            PlatformSettings plat => plat.FieldMapping,
            _ => null
        };

        if (fieldMapping is null || fieldMapping.Count == 0)
            return Result.Fail<PreviewMappingResult>("NO_FIELD_MAPPING_CONFIGURED");

        // Validate the mapping definition
        var validationErrors = FieldMappingEngine.Validate(fieldMapping);

        // Apply the mapping to the sample payload
        var mappingResult = FieldMappingEngine.Apply(fieldMapping, query.SamplePayloadJson);

        return Result.Ok(new PreviewMappingResult(
            MappedLead:       mappingResult.MappedValues,
            FieldErrors:      mappingResult.Errors,
            ValidationErrors: validationErrors));
    }
}
