using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Infrastructure;

namespace Sankore.Modules.Administration.Features.Users.Invitation;

/// <summary>
/// Attribue le matricule suivant d'un tenant : AGT-0001, AGT-0002… Le plus grand numéro déjà
/// donné plus un, et non le nombre d'utilisateurs : un matricule ne se réutilise pas après une
/// désactivation. Deux créations simultanées peuvent viser le même numéro ; l'index unique
/// (tenant, matricule) refuse alors la seconde plutôt que d'attribuer un doublon.
/// </summary>
internal sealed class EmployeeNumberAllocator(AdministrationDbContext db)
{
    public const string Prefix = "AGT-";

    public async Task<string> NextAsync(Guid tenantId, CancellationToken ct)
    {
        var numbers = await db.Users
            .IgnoreQueryFilters()
            .Where(u => u.TenantId == tenantId && u.EmployeeNumber != null && u.EmployeeNumber.StartsWith(Prefix))
            .Select(u => u.EmployeeNumber!)
            .ToListAsync(ct);

        var max = numbers
            .Select(n => int.TryParse(n[Prefix.Length..], out var v) ? v : 0)
            .DefaultIfEmpty(0)
            .Max();

        return $"{Prefix}{max + 1:0000}";
    }
}
