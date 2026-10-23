namespace Sankore.Modules.Administration.Features.PaidModules;

using Microsoft.AspNetCore.Http;
using Sankore.Modules.Administration.Infrastructure.PaidModules;

/// <summary>Where a tenant stands with one module.</summary>
public enum TenantModuleState
{
    /// <summary>Never asked for.</summary>
    None,
    Requested,
    Active,
    Rejected,
    Cancelled,
    Deactivated,
}

/// <summary>
/// One period of a module for the tenant. The platform administrator who decided is deliberately
/// absent: the tenant reads the decision and its reason, not who at the platform took it.
/// </summary>
public sealed record TenantModuleSubscriptionDto(
    Guid Id,
    TenantModuleState Status,
    string Source,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RequestedAt,
    string? RequestedByEmail,
    decimal? AcceptedMonthlyPrice,
    string? AcceptedCurrency,
    DateTimeOffset? DecidedAt,
    string? DecisionReason,
    DateTimeOffset? CancelledAt,
    DateTimeOffset? ActivatedAt,
    DateTimeOffset? DeactivatedAt,
    string? DeactivatedByEmail);

/// <param name="Current">The open period if there is one, else the latest; null when never asked for.</param>
public sealed record TenantModuleDto(
    string Code,
    string Label,
    string Description,
    decimal MonthlyPrice,
    string Currency,
    bool IsAvailable,
    TenantModuleState State,
    TenantModuleSubscriptionDto? Current,
    IReadOnlyList<TenantModuleSubscriptionDto> History);

public sealed record TenantModulesDto(IReadOnlyList<TenantModuleDto> Modules);

/// <summary>
/// Error codes of this area. The registry's own codes (<c>MODULE_PRICE_CHANGED</c>,
/// <c>MODULE_REQUEST_PENDING</c>…) are relayed as they are; these are the ones the API adds.
/// </summary>
public static class TenantModulesErrors
{
    public const string ModuleUnknown = "MODULE_UNKNOWN";
    public const string RegistryUnavailable = HttpTenantModuleRegistry.Unavailable;
    public const string UserNotFound = "USER_NOT_FOUND";

    /// <summary>The registry's codes, relayed. Listed so the endpoint maps each to its status.</summary>
    internal static readonly HashSet<string> NotFound =
        [ModuleUnknown, "TENANT_NOT_FOUND", "MODULE_SUBSCRIPTION_NOT_FOUND", UserNotFound];

    internal static readonly HashSet<string> BadRequest = ["INVALID_ACTOR"];

    internal static IResult ToProblem(string error, string? detail = null)
    {
        var body = new { error, detail };
        if (error == RegistryUnavailable) return Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
        if (NotFound.Contains(error)) return Results.NotFound(body);
        if (BadRequest.Contains(error)) return Results.BadRequest(body);
        return Results.Conflict(body);
    }

    internal static TenantModuleDto ToDto(this RegistryTenantModule m) => new(
        m.Code, m.Label, m.Description, m.MonthlyPrice, m.Currency, m.IsAvailable,
        ParseState(m.State), m.Current?.ToDto(), m.History.Select(h => h.ToDto()).ToList());

    private static TenantModuleSubscriptionDto ToDto(this RegistryModuleSubscription s) => new(
        s.Id, ParseState(s.Status), s.Source, s.CreatedAt, s.RequestedAt, s.RequestedByEmail,
        s.AcceptedMonthlyPrice, s.AcceptedCurrency, s.DecidedAt, s.DecisionReason, s.CancelledAt,
        s.ActivatedAt, s.DeactivatedAt, s.DeactivatedByEmail);

    private static TenantModuleState ParseState(string value) =>
        Enum.TryParse<TenantModuleState>(value, ignoreCase: true, out var state) ? state : TenantModuleState.None;
}
