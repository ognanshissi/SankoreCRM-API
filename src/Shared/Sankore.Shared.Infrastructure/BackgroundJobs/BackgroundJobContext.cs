namespace Sankore.Shared.Infrastructure.BackgroundJobs;

using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

/// <summary>
/// AsyncLocal-based ambient context for background jobs (Hangfire, etc.)
/// that run outside the HTTP pipeline. When set, the DI factory registrations
/// for <see cref="ICurrentUser"/> and <see cref="ITenantContext"/> return
/// these values instead of the HTTP-based implementations.
/// </summary>
public static class BackgroundJobContext
{
    private static readonly AsyncLocal<ICurrentUser?> _user = new();
    private static readonly AsyncLocal<ITenantContext?> _tenant = new();

    public static ICurrentUser? CurrentUser => _user.Value;
    public static ITenantContext? CurrentTenant => _tenant.Value;

    /// <summary>
    /// Establishes a SYSTEM-level execution scope for the current async flow.
    /// Dispose the returned handle to clear the context.
    /// </summary>
    public static IDisposable SetScope(Guid tenantId, Guid userId, string displayName)
    {
        _user.Value = new SystemCurrentUser(userId, tenantId, displayName);
        _tenant.Value = new FixedTenantContext(tenantId);
        return new ContextScope();
    }

    private sealed class ContextScope : IDisposable
    {
        public void Dispose()
        {
            _user.Value = null;
            _tenant.Value = null;
        }
    }
}
