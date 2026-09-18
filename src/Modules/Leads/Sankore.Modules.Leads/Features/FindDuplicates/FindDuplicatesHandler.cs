namespace Sankore.Modules.Leads.Features.FindDuplicates;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class FindDuplicatesHandler(LeadsDbContext db)
    : IRequestHandler<FindDuplicatesQuery, Result<IReadOnlyList<DuplicateMatchResult>>>
{
    public async Task<Result<IReadOnlyList<DuplicateMatchResult>>> Handle(
        FindDuplicatesQuery query, CancellationToken ct)
    {
        var probe = MatchProbe.From(
            query.PhoneNumber, query.Email, query.NationalId,
            query.CustomerReference, query.FullName,
            query.DateOfBirth, query.Latitude, query.Longitude);

        // At least one strong identifier signal is required for a meaningful search.
        if (probe.PhoneDigits is null && probe.EmailNorm is null
            && probe.NationalId is null && probe.CustomerReference is null)
        {
            return Result.Fail<IReadOnlyList<DuplicateMatchResult>>(
                "At least one of phoneNumber, email, nationalId, or customerReference is required.");
        }

        var phoneDigits    = probe.PhoneDigits;
        var emailNorm      = probe.EmailNorm;
        var nationalId     = probe.NationalId;
        var customerRef    = probe.CustomerReference;

        // DB pre-filter: candidates that match at least one strong signal.
        // Phone suffix match via EndsWith (translates to SQL LIKE '%<digits>').
        // Name / DOB / location are additive signals scored in-memory only.
        var candidates = await db.Leads
            .Where(l =>
                l.Status != LeadStatus.Lost &&
                l.Status != LeadStatus.Archived &&
                l.Status != LeadStatus.Disqualified &&
                ((phoneDigits != null && l.PhoneNumber.EndsWith(phoneDigits)) ||
                 (emailNorm != null && l.Email != null && l.Email.ToLower() == emailNorm) ||
                 (nationalId != null && l.NationalId != null && l.NationalId.ToLower() == nationalId.ToLower()) ||
                 (customerRef != null && l.CustomerReference != null && l.CustomerReference.ToLower() == customerRef.ToLower())))
            .ToListAsync(ct);

        var scorer = new IdentityMatchScorer();

        var results = candidates
            .Select(l => scorer.Score(l, probe))
            .Where(r => r is not null)
            .Select(r => r!)
            .OrderByDescending(r => r.ConfidenceScore)
            .ToList();

        return Result.Ok<IReadOnlyList<DuplicateMatchResult>>(results);
    }
}
