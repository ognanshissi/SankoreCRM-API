namespace Sankore.Modules.Leads.Features.Household.GetHousehold;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>
/// The household of a lead (BNF-B-03, BNF-B-05).
/// </summary>
/// <param name="IncludePepDetail">
/// Whether the caller holds <c>lead:pep:read</c>. Decided by the ENDPOINT from the caller's
/// claims, not by the handler — a query that worked it out itself would be a permission check
/// buried where nobody reviewing authorization would look for it.
/// </param>
internal sealed record GetHouseholdQuery(Guid LeadId, bool IncludePepDetail)
    : IRequest<Result<LeadHouseholdDto>>;
