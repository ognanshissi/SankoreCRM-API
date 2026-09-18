namespace Sankore.Modules.Leads.Features.FindDuplicates;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class FindDuplicatesHandler(LeadsDbContext db)
    : IRequestHandler<FindDuplicatesQuery, Result<IReadOnlyList<LeadDuplicateDto>>>
{
    public async Task<Result<IReadOnlyList<LeadDuplicateDto>>> Handle(
        FindDuplicatesQuery query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query.PhoneNumber) && string.IsNullOrWhiteSpace(query.Email))
            return Result.Fail<IReadOnlyList<LeadDuplicateDto>>("At least one of phoneNumber or email is required.");

        var phone = query.PhoneNumber?.Trim();
        var email = query.Email?.Trim().ToLowerInvariant();

        var leads = await db.Leads
            .Where(l =>
                (phone != null && l.PhoneNumber == phone) ||
                (email != null && l.Email != null && l.Email.ToLower() == email))
            .OrderBy(l => l.CapturedAt)
            .ToListAsync(ct);

        var results = leads.Select(l =>
        {
            var matchedOn = new List<string>();
            if (phone != null && l.PhoneNumber == phone) matchedOn.Add("phone");
            if (email != null && l.Email != null &&
                string.Equals(l.Email, email, StringComparison.OrdinalIgnoreCase)) matchedOn.Add("email");

            return new LeadDuplicateDto(
                LeadId:    l.Id,
                FullName:  l.FullName,
                PhoneNumber: l.PhoneNumber,
                Email:     l.Email,
                Status:    l.Status,
                Source:    l.Source,
                CapturedAt: l.CapturedAt,
                MatchedOn: matchedOn);
        }).ToList();

        return Result.Ok<IReadOnlyList<LeadDuplicateDto>>(results);
    }
}
