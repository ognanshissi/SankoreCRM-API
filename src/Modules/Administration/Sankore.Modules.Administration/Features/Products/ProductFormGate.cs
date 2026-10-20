namespace Sankore.Modules.Administration.Features.Products;

using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Domain.Forms;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.PublicApi.Forms;
using Sankore.Shared.Kernel;

/// <summary>
/// The product form applied to a product write. Unlike the lead and client gates it reads the form
/// in this module's own context — same database, same transaction — so there is no outage posture
/// to choose: a failure to read is a failure of the write itself.
/// </summary>
/// <remarks>
/// Every product write comes from the catalogue screen, so the fields the tenant made required are
/// always enforced.
/// </remarks>
internal static class ProductFormGate
{
    public static async Task<Result<string?>> ApplyAsync(
        AdministrationDbContext db,
        string? currentJson,
        IReadOnlyDictionary<string, JsonElement>? changes,
        IReadOnlyDictionary<string, string?> nativeValues,
        bool isCreate,
        CancellationToken ct)
    {
        var layout = await db.EntityFormLayouts.FirstOrDefaultAsync(f => f.Entity == FormEntity.Product, ct);
        var form = EntityFormResolver.Resolve(FormEntity.Product, layout?.Version ?? 0, layout?.Layout);

        if (isCreate)
        {
            var missing = CustomFieldValues.MissingRequiredNatives(form, nativeValues);
            if (missing.Count > 0)
                return Result.Fail<string?>($"FIELD_REQUIRED:{missing[0]}");
        }

        return CustomFieldValues.Apply(form, currentJson, changes, nativeValues, enforceRequired: true, isCreate);
    }

    public static IReadOnlyDictionary<string, string?> NativeValues(
        string name, string code, ProductCategory category, string? description, DateOnly? effectiveFrom,
        string? businessProductId, string? businessPlatformName) =>
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["name"] = name,
            ["code"] = code,
            ["category"] = category.ToString(),
            ["description"] = description,
            ["effectiveFrom"] = effectiveFrom?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["businessProductId"] = businessProductId,
            ["businessPlatformName"] = businessPlatformName,
        };

    public static ProductDto ToDto(ProductSpeciality p) => new(
        p.Id, p.Name, p.Code, p.Category,
        p.Description, p.ParametersJson,
        p.IsActive, p.EffectiveFrom, p.EffectiveTo,
        p.BusinessProductId, p.BusinessPlatformName,
        p.BeneficiaryPolicy, p.AllowMultipleHoldings, p.KycRemotePolicy,
        CustomFieldValues.Read(p.CustomFieldsJson));
}
