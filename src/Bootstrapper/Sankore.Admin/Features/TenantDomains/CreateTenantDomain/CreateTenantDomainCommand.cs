using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Domain;
using Sankore.Admin.Infrastructure;
using Sankore.Shared.Infrastructure.Tenants;

namespace Sankore.Admin.Features.TenantDomains.CreateTenantDomain;

public record CreateTenantDomainCommand(
    Guid TenantId,
    string Fqdn,
    bool IsPrimary,
    DateTimeOffset? ValidFrom,
    DateTimeOffset? ValidTo) : IRequest<CreateTenantDomainResult>;

public record CreateTenantDomainResponse(Guid Id);

public enum CreateTenantDomainError { None, TenantNotFound, InvalidOrigin, Conflict }

public sealed record CreateTenantDomainResult(Guid? Id, CreateTenantDomainError Error, string? Message)
{
    public static CreateTenantDomainResult Ok(Guid id) => new(id, CreateTenantDomainError.None, null);
    public static CreateTenantDomainResult Fail(CreateTenantDomainError error, string message) => new(null, error, message);
}

/// <summary>
/// Refusals are RESULTS, not exceptions: this handler used to throw InvalidOperationException for
/// an unknown tenant or a taken domain, and with no exception handler in this host that reached
/// the caller as a bare 500 — an operator typing a duplicate saw "server error".
/// </summary>
internal sealed class CreateTenantDomainHandler(AdminDbContext db)
    : IRequestHandler<CreateTenantDomainCommand, CreateTenantDomainResult>
{
    public async Task<CreateTenantDomainResult> Handle(CreateTenantDomainCommand request, CancellationToken ct)
    {
        var tenantExists = await db.Tenants.AnyAsync(t => t.Id == request.TenantId, ct);
        if (!tenantExists)
            return CreateTenantDomainResult.Fail(CreateTenantDomainError.TenantNotFound, "Tenant introuvable.");

        // Same rule as tenants.fqdn: what a browser sends is location.origin, so anything else is
        // a domain that can never match.
        if (!TenantFqdn.IsOrigin(request.Fqdn, out _))
            return CreateTenantDomainResult.Fail(
                CreateTenantDomainError.InvalidOrigin,
                "Saisissez une origine complète, schéma compris (https://crm.exemple.com), sans chemin.");

        var fqdn = TenantFqdn.Normalize(request.Fqdn);

        // Against the tenants' main origins too: resolution checks those first, so a secondary
        // domain equal to another tenant's main one would never be reached.
        if (await db.Tenants.AnyAsync(t => t.Fqdn == fqdn, ct))
            return CreateTenantDomainResult.Fail(
                CreateTenantDomainError.Conflict, $"{fqdn} est déjà l’adresse principale d’un tenant.");

        // ix_tenant_domains_fqdn is unique over EVERY row, deactivated ones included, so checking
        // active rows only — as before — let the insert reach the index and fail as a 500.
        var existing = await db.TenantDomains.AsNoTracking()
            .Where(d => d.Fqdn == fqdn)
            .Select(d => new { d.IsActive })
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
            return CreateTenantDomainResult.Fail(
                CreateTenantDomainError.Conflict,
                existing.IsActive
                    ? $"{fqdn} est déjà attribué à un tenant."
                    : $"{fqdn} a déjà été utilisé puis désactivé ; il ne peut pas être réattribué.");

        if (request.ValidTo is { } to && to <= (request.ValidFrom ?? DateTimeOffset.UtcNow))
            return CreateTenantDomainResult.Fail(
                CreateTenantDomainError.InvalidOrigin, "La fin de validité doit suivre son début.");

        // Demote existing primary before assigning a new one.
        if (request.IsPrimary)
        {
            var currentPrimary = await db.TenantDomains
                .FirstOrDefaultAsync(d => d.TenantId == request.TenantId && d.IsPrimary && d.IsActive, ct);

            currentPrimary?.UnsetPrimary();
        }

        var domain = TenantDomain.Create(
            request.TenantId, fqdn, request.IsPrimary,
            request.ValidFrom, request.ValidTo);

        db.TenantDomains.Add(domain);
        await db.SaveChangesAsync(ct);

        return CreateTenantDomainResult.Ok(domain.Id);
    }
}
