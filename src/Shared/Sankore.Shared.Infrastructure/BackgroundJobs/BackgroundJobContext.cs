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
        var scope = new ContextScope(_user.Value, _tenant.Value);
        _user.Value = new SystemCurrentUser(userId, tenantId, displayName);
        _tenant.Value = new FixedTenantContext(tenantId);
        return scope;
    }

    /// <summary>
    /// Restores what was there before, rather than clearing: scopes nest since a job may replay a
    /// command in ANOTHER tenant (sandbox lot 5, a refresh of the test mode from production) and
    /// must find its own scope again afterwards.
    /// </summary>
    private sealed class ContextScope(ICurrentUser? previousUser, ITenantContext? previousTenant) : IDisposable
    {
        public void Dispose()
        {
            _user.Value = previousUser;
            _tenant.Value = previousTenant;
        }
    }
}
