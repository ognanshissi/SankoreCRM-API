namespace Sankore.Modules.Integration.Features.Telephony.UserLinks.GetDirectory;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

/// <summary>
/// Reads the provider's directory and proposes a pairing for each account (TEL-B-05).
///
/// <para>
/// The provider is the authority on which accounts exist; this module is the authority on which
/// are already linked. The proposal is the intersection of the two plus an address lookup in M12,
/// and nothing here writes a row.
/// </para>
/// </summary>
internal sealed class GetTelephonyDirectoryHandler(
    IntegrationDbContext db,
    TelephonyAccess access,
    IAdministrationModule administration,
    ICurrentUser currentUser,
    ILogger<GetTelephonyDirectoryHandler> logger)
    : IRequestHandler<GetTelephonyDirectoryQuery, Result<TelephonyDirectoryDto>>
{
    public async Task<Result<TelephonyDirectoryDto>> Handle(
        GetTelephonyDirectoryQuery query, CancellationToken ct)
    {
        var use = await access.ResolvePortAsync(
            currentUser.TenantId, IntegrationCapability.UserDirectory, ct);

        if (use.IsFailure)
            return Result.Fail<TelephonyDirectoryDto>(use.Code!);

        var users = await use.Value.Port.ListUsersAsync(ct);

        if (users.IsFailure)
        {
            // The provider being unreachable is not this screen's fault to hide: an empty
            // directory would read as "your provider has no users", which is the one conclusion
            // that sends an administrator to delete working links.
            logger.LogWarning(
                "Telephony directory unavailable for tenant {TenantId}: {Family}/{Code}.",
                currentUser.TenantId, users.Family, users.Code);

            return Result.Fail<TelephonyDirectoryDto>(IntegrationErrors.Unavailable);
        }

        var connectionId = use.Value.Connection.Id;

        // The links of THIS connection only. A tenant that changed provider keeps the old rows —
        // they explain the old journal — and proposing against them would offer an administrator
        // a pairing with an account that no longer exists.
        var existing = await db.TelUserLinks
            .Where(l => l.ConnectionId == connectionId && l.Status != TelUserLinkStatus.Disabled)
            .Select(l => new { l.ExternalUserId, l.CrmUserId })
            .ToListAsync(ct);

        var linkedByExternal = existing.ToDictionary(l => l.ExternalUserId, l => l.CrmUserId);

        // Already-linked CRM users are excluded from the proposals: one user holds at most one
        // account, so proposing a second would be proposing something the unique index refuses.
        var linkedCrmUsers = existing.Select(l => l.CrmUserId).ToHashSet();

        var matches = await administration.FindUsersByEmailAsync(
            currentUser.TenantId,
            users.Value.Select(u => u.Email).Where(e => e is not null).Select(e => e!).ToList(),
            ct);

        // Grouped rather than keyed: two CRM accounts answering to one address is exactly the
        // case that must produce NO proposal, and a dictionary build would have thrown on it.
        var byEmail = matches
            .GroupBy(m => m.Email, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var ambiguous = new List<string>();
        var entries = new List<TelephonyDirectoryEntryDto>(users.Value.Count);

        foreach (var user in users.Value.OrderBy(u => u.DisplayName ?? u.Id.Value, StringComparer.OrdinalIgnoreCase))
        {
            UserEmailMatch? proposal = null;

            if (user.Email is { } email && byEmail.TryGetValue(email, out var candidates))
            {
                if (candidates.Count > 1)
                {
                    ambiguous.Add(email);
                }
                else if (!linkedCrmUsers.Contains(candidates[0].UserId))
                {
                    proposal = candidates[0];
                }
            }

            entries.Add(new TelephonyDirectoryEntryDto(
                ExternalUserId: user.Id.Value,
                Email: user.Email,
                DisplayName: user.DisplayName,
                IsActiveAtProvider: user.IsActive,
                LinkedCrmUserId: linkedByExternal.TryGetValue(user.Id.Value, out var crmUserId)
                    ? crmUserId
                    : null,
                ProposedCrmUserId: proposal?.UserId,
                ProposedCrmUserName: proposal?.FullName,
                ProposedCrmUserIsDisabled: proposal is { IsActive: false }));
        }

        return Result.Ok(new TelephonyDirectoryDto(
            entries, ambiguous.Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
    }
}
