namespace Sankore.Shared.Kernel.Authorization;

/// <summary>
/// Resolves the set of agencies a user is allowed to see or act upon.
///
/// The perimeter is: the user's own agency + every active descendant agency
/// (BFS over <c>Agency.ParentAgencyId</c>) + every agency granted through an
/// active <c>PermissionAttribution</c> whose scope type is "Agency" (and the
/// descendants of those agencies too).
///
/// Implemented by the Administration module (the only module that owns the
/// agency tree); consumed by every other module through this Kernel contract
/// so that no module has to reference Administration's domain.
/// </summary>
public interface IAgencyScopeProvider
{
    /// <summary>
    /// Agencies visible to <paramref name="userId"/> inside <paramref name="tenantId"/>.
    /// </summary>
    /// <returns>
    /// <c>null</c> means UNRESTRICTED: the caller is a super-user (or a user with
    /// no agency at all, i.e. a tenant-wide account) and may see every agency of
    /// the tenant. Callers must therefore treat <c>null</c> as "apply no agency
    /// filter", never as "no access".
    /// An EMPTY set means the opposite: the user exists but sees nothing
    /// (also returned for an unknown user id, so an unknown caller is denied).
    /// </returns>
    Task<IReadOnlySet<Guid>?> GetAccessibleAgencyIdsAsync(Guid tenantId, Guid userId, CancellationToken ct);

    /// <summary>
    /// True when <paramref name="agencyId"/> is inside the user's perimeter.
    /// Always true when <see cref="GetAccessibleAgencyIdsAsync"/> returns
    /// <c>null</c> (unrestricted super-user).
    /// </summary>
    Task<bool> CanAccessAgencyAsync(Guid tenantId, Guid userId, Guid agencyId, CancellationToken ct);
}
