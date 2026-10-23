using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Domain;
using Sankore.Admin.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Admin.Features.Modules.TenantSide;

/// <summary>One module as a tenant's « Modules » page shows it: the offer, where the tenant stands, and the past.</summary>
/// <param name="Current">The open row if there is one, else the most recent; null when never touched.</param>
public sealed record TenantModuleResponse(
    string Code,
    string Label,
    string Description,
    decimal MonthlyPrice,
    string Currency,
    bool IsAvailable,
    TenantModuleState State,
    TenantModuleSubscriptionResponse? Current,
    IReadOnlyList<TenantModuleSubscriptionResponse> History);

public sealed record TenantModulesResponse(Guid TenantId, IReadOnlyList<TenantModuleResponse> Modules);

public static class ListTenantModulesEndpoint
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/", async (Guid tenantId, ISender sender, CancellationToken ct) =>
            {
                var response = await sender.Send(new ListTenantModulesQuery(tenantId), ct);
                return response is null
                    ? Results.NotFound(new { error = ModuleErrors.TenantNotFound })
                    : Results.Ok(response);
            })
            .WithName("ListTenantModules")
            .WithSummary("A tenant's modules: offer, current state and history")
            .Produces<TenantModulesResponse>()
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();
    }
}

public sealed record ListTenantModulesQuery(Guid TenantId) : IRequest<TenantModulesResponse?>;

internal sealed class ListTenantModulesHandler(AdminDbContext db)
    : IRequestHandler<ListTenantModulesQuery, TenantModulesResponse?>
{
    public async Task<TenantModulesResponse?> Handle(ListTenantModulesQuery request, CancellationToken ct)
    {
        if (!await db.Tenants.AnyAsync(t => t.Id == request.TenantId, ct))
            return null;

        var definitions = await db.ModuleDefinitions.AsNoTracking().ToListAsync(ct);
        var rows = await db.TenantModuleSubscriptions.AsNoTracking()
            .Where(s => s.TenantId == request.TenantId)
            .ToListAsync(ct);

        // TenantModules.All fixes the order: the page lists the modules the same way every time.
        var modules = definitions
            .OrderBy(d => IndexOf(d.Code))
            .ThenBy(d => d.Code, StringComparer.Ordinal)
            .Select(d => Describe(d, rows.Where(r => r.ModuleCode == d.Code)))
            .ToList();

        return new TenantModulesResponse(request.TenantId, modules);
    }

    private static TenantModuleResponse Describe(ModuleDefinition definition, IEnumerable<TenantModuleSubscription> rows)
    {
        var history = rows.OrderByDescending(r => r.CreatedAt).ToList();
        var current = history.FirstOrDefault(r => r.IsOpen) ?? history.FirstOrDefault();

        return new TenantModuleResponse(
            definition.Code,
            definition.Label,
            definition.Description,
            definition.MonthlyPrice,
            definition.Currency,
            definition.IsAvailable,
            current is null ? TenantModuleState.None : Enum.Parse<TenantModuleState>(current.Status.ToString()),
            current is null ? null : TenantModuleSubscriptionResponse.From(current),
            history.Select(TenantModuleSubscriptionResponse.From).ToList());
    }

    private static int IndexOf(string code)
    {
        for (var i = 0; i < TenantModules.All.Count; i++)
            if (TenantModules.All[i] == code) return i;
        return int.MaxValue;
    }
}
