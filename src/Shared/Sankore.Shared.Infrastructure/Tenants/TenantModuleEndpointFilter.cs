namespace Sankore.Shared.Infrastructure.Tenants;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Shared.Kernel;

/// <summary>
/// Closes a route — usually a whole <c>MapGroup</c> — to a tenant that has none of the given paid
/// modules (<see cref="TenantModules"/>). Answers <c>403 TENANT_MODULE_DISABLED</c>, naming the
/// modules, so the front-end can say « this module is not active for your institution » instead
/// of a generic refusal.
///
/// <para>
/// Separate from permissions on purpose. A permission says WHO may act; a module says whether
/// the feature exists for this tenant at all. Withholding permission claims would not do: some
/// module routes share a permission with the socle (client groups are read with
/// <c>customers:read</c>), and a token already issued would keep its claims until refreshed.
/// </para>
///
/// <para>
/// Telephony is socle and must never carry this filter.
/// </para>
///
/// <para>
/// GET and HEAD also pass for a module the tenant DEACTIVATED (<see cref="ITenantModules.IsReadableAsync(string, CancellationToken)"/>):
/// its screens stay readable. A route that reads by POST, or that writes on a GET, must not rely on
/// this filter alone.
/// </para>
/// </summary>
public sealed class TenantModuleEndpointFilter(IReadOnlyList<string> anyOf) : IEndpointFilter
{
    public IReadOnlyList<string> AnyOf { get; } = anyOf.Count > 0
        ? anyOf
        : throw new ArgumentException("Name at least one module.", nameof(anyOf));

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var modules = context.HttpContext.RequestServices.GetRequiredService<ITenantModules>();
        var ct = context.HttpContext.RequestAborted;

        // A deactivated module stays open for CONSULTATION: a read goes through while the module
        // is in force OR deactivated, a write only while it is in force.
        var isRead = HttpMethods.IsGet(context.HttpContext.Request.Method)
                     || HttpMethods.IsHead(context.HttpContext.Request.Method);

        foreach (var module in AnyOf)
            if (isRead ? await modules.IsReadableAsync(module, ct) : await modules.IsEnabledAsync(module, ct))
                return await next(context);

        return TenantModuleResults.Disabled(AnyOf);
    }
}

/// <summary>
/// The 403 bodies of the module gate, one shape for the route filter and for the handlers that
/// refuse a category: <c>{ error, code, modules }</c>, so the front-end reads the code the same
/// way wherever the refusal comes from.
/// </summary>
public static class TenantModuleResults
{
    public static IResult Disabled(IReadOnlyList<string> modules) => Results.Json(
        new
        {
            error = "Ce module n'est pas activé pour votre institution.",
            code = TenantModuleErrors.Disabled,
            modules,
        },
        statusCode: StatusCodes.Status403Forbidden);

    /// <summary>Names the module when the caller still has the category at hand.</summary>
    public static IResult CategoryDisabled(ProductCategory? category = null) => Results.Json(
        new
        {
            error = "Cette catégorie de produit relève d'un module qui n'est pas activé pour votre institution.",
            code = TenantModuleErrors.CategoryDisabled,
            modules = category is { } c && ProductCategories.ModuleOf(c) is { } module ? new[] { module } : [],
        },
        statusCode: StatusCodes.Status403Forbidden);
}

public static class TenantModuleEndpointFilterExtensions
{
    /// <summary>
    /// Requires at least one of <paramref name="anyOf"/> to be in force for the current tenant.
    /// Several modules for surfaces both share (integration mappings, commands, reconciliation).
    /// </summary>
    public static TBuilder RequireTenantModule<TBuilder>(this TBuilder builder, params string[] anyOf)
        where TBuilder : IEndpointConventionBuilder
    {
        var unknown = anyOf.FirstOrDefault(m => !TenantModules.IsKnown(m));
        if (unknown is not null)
            throw new ArgumentException($"'{unknown}' is not a module.", nameof(anyOf));

        return builder.AddEndpointFilter(new TenantModuleEndpointFilter(anyOf));
    }
}
