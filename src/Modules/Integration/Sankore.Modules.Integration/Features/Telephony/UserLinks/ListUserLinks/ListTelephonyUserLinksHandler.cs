namespace Sankore.Modules.Integration.Features.Telephony.UserLinks.ListUserLinks;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

internal sealed class ListTelephonyUserLinksHandler(
    IntegrationDbContext db,
    IAdministrationModule administration,
    ICurrentUser currentUser)
    : IRequestHandler<ListTelephonyUserLinksQuery, Result<IReadOnlyList<TelephonyUserLinkDto>>>
{
    public async Task<Result<IReadOnlyList<TelephonyUserLinkDto>>> Handle(
        ListTelephonyUserLinksQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        // No tenant predicate: the global query filter scopes it. And no connection filter
        // either — a tenant that changed provider should see the old links, which are what
        // explain a journal nobody is attributing any more.
        var rows = db.TelUserLinks.AsQueryable();

        if (!query.IncludeDisabled)
            rows = rows.Where(l => l.Status != TelUserLinkStatus.Disabled);

        // Broken first: it is the only status that asks somebody to do something.
        var links = await rows
            .OrderBy(l => l.Status == TelUserLinkStatus.Broken ? 0 : 1)
            .ThenBy(l => l.ExternalEmail)
            .ThenBy(l => l.Id)
            .ToListAsync(ct);

        if (links.Count == 0) return Result.Ok<IReadOnlyList<TelephonyUserLinkDto>>([]);

        // One call for every name rather than one per row. An id that no longer resolves is
        // simply absent from the dictionary and the DTO carries null — a deleted account leaves
        // its link behind on purpose.
        var names = await administration.GetUserNamesAsync(
            currentUser.TenantId, links.Select(l => l.CrmUserId).Distinct().ToList(), ct);

        return Result.Ok<IReadOnlyList<TelephonyUserLinkDto>>(links
            .Select(l => new TelephonyUserLinkDto(
                l.Id,
                l.CrmUserId,
                names.GetValueOrDefault(l.CrmUserId),
                l.ExternalUserId,
                l.ExternalEmail,
                l.DefaultCallerNumber,
                l.Status,
                l.StatusReason,
                l.LinkedAt,
                l.Version))
            .ToList());
    }
}
