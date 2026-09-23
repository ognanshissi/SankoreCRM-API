namespace Sankore.Modules.Leads.Features.LeadSources.PreviewMapping;

using MediatR;
using Sankore.Modules.Leads.Features.LeadSources.Mapping;
using Sankore.Shared.Kernel;

/// <summary>
/// Dry-run a field mapping against a sample payload (US-F13.37-BE-07).
/// Nothing is persisted — this is a pure preview.
/// </summary>
internal sealed record PreviewMappingQuery(
    Guid SourceId,
    string SamplePayloadJson)
    : IRequest<Result<PreviewMappingResult>>;

internal sealed record PreviewMappingResult(
    IReadOnlyDictionary<string, string?> MappedLead,
    IReadOnlyList<MappingFieldError> FieldErrors,
    IReadOnlyList<MappingValidationError> ValidationErrors);
