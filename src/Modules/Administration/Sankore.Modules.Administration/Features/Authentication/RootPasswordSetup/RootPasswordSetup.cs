using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.Infrastructure.Identity;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Authentication.RootPasswordSetup;

/// <summary>
/// See <see cref="IRootPasswordSetup"/>. The link is an ACTIVATION token for the root account, and
/// <c>AccountActivationHandler</c> accepts it for exactly this account: the institution's system
/// account that has never signed in (<see cref="IsFirstPasswordPending"/>). Every other active account
/// is still refused, so this opens no way to reset someone's password with an old invitation.
/// </summary>
internal sealed class RootPasswordSetup(
    AdministrationDbContext db,
    UserManager<AppUser> userManager,
    ITenantStore tenantStore,
    IOptions<IdentityTokenOptions> tokenOptions,
    TimeProvider clock) : IRootPasswordSetup
{
    /// <summary>
    /// The institution's root (system, super-user) account, active and never signed in: its password
    /// is the random one the starting-data load gave it, which nobody knows.
    /// </summary>
    public static bool IsFirstPasswordPending(AppUser user) =>
        user is { IsSuperUser: true, AccountType: UserAccountType.System, Status: UserStatus.Active, LastLoginAt: null };

    public async Task<RootPasswordSetupLink?> IssueAsync(Guid tenantId, CancellationToken ct)
    {
        var user = await db.Users.IgnoreQueryFilters()
            .Where(u => u.TenantId == tenantId && u.IsSuperUser && u.AccountType == UserAccountType.System)
            .FirstOrDefaultAsync(ct);
        if (user is null) return null;

        if (!IsFirstPasswordPending(user))
            return new RootPasswordSetupLink(user.Id, user.Email!, null, AlreadySignedIn: true, null);

        var tenant = await tenantStore.GetAsync(tenantId, ct);
        if (tenant is null || string.IsNullOrWhiteSpace(tenant.Fqdn)) return null;

        var token = await userManager.GenerateUserTokenAsync(user, ActivationTokens.ProviderName, ActivationTokens.Purpose);
        var url = $"{tenant.Fqdn.TrimEnd('/')}/auth/account-activation?token={Uri.EscapeDataString(token)}"
                  + $"&userId={user.Id}&RequestType=AccountActivation";

        return new RootPasswordSetupLink(user.Id, user.Email!, url, AlreadySignedIn: false,
            clock.GetUtcNow() + tokenOptions.Value.ActivationTokenLifespan);
    }
}
