using Microsoft.EntityFrameworkCore;
using Npgsql;
using Sankore.Admin.Domain;
using Sankore.Admin.Infrastructure.Configurations;

namespace Sankore.Admin.Features.Modules;

/// <summary>
/// Error codes of the module routes. Sankore.Api relays them to the tenant's front-end as they
/// are, so they are a contract: never rename one.
/// </summary>
public static class ModuleErrors
{
    public const string ModuleUnknown = "MODULE_UNKNOWN";
    public const string ModuleUnavailable = "MODULE_UNAVAILABLE";
    public const string PriceChanged = "MODULE_PRICE_CHANGED";
    public const string RequestPending = "MODULE_REQUEST_PENDING";
    public const string AlreadyActive = "MODULE_ALREADY_ACTIVE";
    public const string NotActive = "MODULE_NOT_ACTIVE";
    public const string RequestNotPending = "MODULE_REQUEST_NOT_PENDING";
    public const string SubscriptionNotFound = "MODULE_SUBSCRIPTION_NOT_FOUND";
    public const string RejectionReasonRequired = "REJECTION_REASON_REQUIRED";
    public const string InvalidDefinition = "INVALID_MODULE_DEFINITION";
    public const string InvalidActor = "INVALID_ACTOR";
    public const string TenantNotFound = "TENANT_NOT_FOUND";
}

/// <summary>
/// What a module command answers. This host has no Result type and maps no exception, so a
/// handler returns the error code and the endpoint translates it (<see cref="ModuleHttp.ToHttp"/>).
/// </summary>
public sealed record ModuleResult(string? Error, Guid? SubscriptionId = null, string? Detail = null)
{
    public bool IsSuccess => Error is null;

    public static ModuleResult Ok(Guid? subscriptionId = null) => new(null, subscriptionId);
    public static ModuleResult Fail(string error, string? detail = null) => new(error, null, detail);
}

/// <summary>The state of one module for one tenant, as a screen shows it.</summary>
public enum TenantModuleState
{
    /// <summary>Never asked for, or nothing open and nothing in the past.</summary>
    None,
    Requested,
    Active,
    Rejected,
    Cancelled,
    Deactivated,
}

public sealed record TenantModuleSubscriptionResponse(
    Guid Id,
    Guid TenantId,
    string ModuleCode,
    ModuleSubscriptionStatus Status,
    ModuleSubscriptionSource Source,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RequestedAt,
    Guid? RequestedByUserId,
    string? RequestedByEmail,
    decimal? AcceptedMonthlyPrice,
    string? AcceptedCurrency,
    DateTimeOffset? DecidedAt,
    Guid? DecidedByAdminId,
    string? DecisionReason,
    DateTimeOffset? CancelledAt,
    DateTimeOffset? ActivatedAt,
    DateTimeOffset? DeactivatedAt,
    string? DeactivatedByEmail)
{
    public static TenantModuleSubscriptionResponse From(TenantModuleSubscription s) => new(
        s.Id, s.TenantId, s.ModuleCode, s.Status, s.Source, s.CreatedAt,
        s.RequestedAt, s.RequestedByUserId, s.RequestedByEmail,
        s.AcceptedMonthlyPrice, s.AcceptedCurrency,
        s.DecidedAt, s.DecidedByAdminId, s.DecisionReason,
        s.CancelledAt, s.ActivatedAt, s.DeactivatedAt, s.DeactivatedByEmail);
}

public sealed record ModuleDefinitionResponse(
    string Code,
    string Label,
    string Description,
    decimal MonthlyPrice,
    string Currency,
    bool IsAvailable,
    DateTimeOffset UpdatedAt)
{
    public static ModuleDefinitionResponse From(ModuleDefinition m) =>
        new(m.Code, m.Label, m.Description, m.MonthlyPrice, m.Currency, m.IsAvailable, m.UpdatedAt);
}

internal static class ModuleHttp
{
    public static IResult ToHttp(this ModuleResult result, Func<ModuleResult, IResult> onSuccess)
    {
        if (result.IsSuccess) return onSuccess(result);

        var body = new { error = result.Error, detail = result.Detail };
        return result.Error switch
        {
            ModuleErrors.ModuleUnknown or ModuleErrors.TenantNotFound or ModuleErrors.SubscriptionNotFound
                => Results.NotFound(body),
            ModuleErrors.RejectionReasonRequired or ModuleErrors.InvalidDefinition or ModuleErrors.InvalidActor
                => Results.BadRequest(body),
            _ => Results.Conflict(body),
        };
    }

    public static IResult NoContent(ModuleResult _) => Results.NoContent();
}

internal static class ModuleQueries
{
    public static Task<TenantModuleSubscription?> FindOpenAsync(
        this IQueryable<TenantModuleSubscription> rows, Guid tenantId, string module, CancellationToken ct) =>
        rows.Where(s => s.TenantId == tenantId && s.ModuleCode == module
                        && (s.Status == ModuleSubscriptionStatus.Requested || s.Status == ModuleSubscriptionStatus.Active))
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// A concurrent request or activation won the race to the filtered unique index. The handlers
    /// read before writing, so reaching this means two writes arrived together.
    /// </summary>
    public static bool IsOpenRowViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
        && pg.ConstraintName == TenantModuleSubscriptionConfiguration.OpenRowIndex;

    public static string ConflictFor(ModuleSubscriptionStatus openStatus) =>
        openStatus == ModuleSubscriptionStatus.Active ? ModuleErrors.AlreadyActive : ModuleErrors.RequestPending;
}
