namespace Sankore.Modules.Integration.Features.RelayAgents.ListRelayAgents;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ListRelayAgentsHandler(IntegrationDbContext db, TimeProvider clock)
    : IRequestHandler<ListRelayAgentsQuery, Result<IReadOnlyList<RelayAgentDto>>>
{
    public async Task<Result<IReadOnlyList<RelayAgentDto>>> Handle(
        ListRelayAgentsQuery query, CancellationToken ct)
    {
        // No tenant predicate and no IgnoreQueryFilters: the DbContext's global query filter
        // scopes this, so another tenant's agents are simply not in the result set. This is the
        // one read side of an aggregate whose other lookups all bypass that filter — the
        // enrolment exchange and the admission check have no tenant context — so the contrast is
        // worth noticing: here there IS a JWT, and the filter is the right mechanism.
        var agents = await db.RelayAgents
            // Revoked last, then by name: the screen's first question is which relays are live.
            .OrderBy(a => a.Status == RelayAgentStatus.Revoked)
            .ThenBy(a => a.Name)
            .ThenBy(a => a.Id)
            .ToListAsync(ct);

        var now = clock.GetUtcNow();

        // Projected through RelayAgentDto.From, which is the single place that decides what leaves
        // this module — and in particular that the certificate thumbprint does not.
        IReadOnlyList<RelayAgentDto> rows =
            agents.Select(a => RelayAgentDto.From(a, now)).ToList();

        return Result.Ok(rows);
    }
}
