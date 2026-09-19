namespace Sankore.Shared.Infrastructure.BackgroundJobs;

using Sankore.Shared.Infrastructure.Auth;

/// <summary>
/// Fixed <see cref="ICurrentUser"/> used by background jobs running under
/// the SYSTEM identity (no HTTP context / JWT available).
/// </summary>
public sealed class SystemCurrentUser(Guid id, Guid tenantId, string displayName) : ICurrentUser
{
    public Guid Id { get; } = id;
    public Guid TenantId { get; } = tenantId;
    public string DisplayName { get; } = displayName;
    public bool IsAuthenticated => true;
    public IReadOnlyList<string> Roles { get; } = ["System"];
}
