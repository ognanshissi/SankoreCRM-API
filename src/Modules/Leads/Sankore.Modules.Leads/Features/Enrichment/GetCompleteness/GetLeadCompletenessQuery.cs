namespace Sankore.Modules.Leads.Features.Enrichment.GetCompleteness;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>
/// What is still missing from a lead's file, and whether it may convert (BNF-B-04).
///
/// <para>
/// A query, so no transaction and no audit row: reading a completeness report changes nothing, and
/// the list carries no sensitive value — subject LABELS and field CODES, never a birth date or an
/// address. That is why it is not gated on <c>lead:pep:read</c> or <c>lead:health:read</c> the way
/// the household and questionnaire reads are, and why a missing health answer is reported as a
/// missing QUESTIONNAIRE rather than as a missing field.
/// </para>
/// </summary>
internal sealed record GetLeadCompletenessQuery(Guid LeadId)
    : IRequest<Result<LeadCompletenessDto>>;
