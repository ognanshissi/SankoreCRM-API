using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Products;

using Sankore.Modules.Administration.Domain;

internal sealed record ProductDto(
    Guid Id,
    string Name,
    string Code,
    ProductCategory Category,
    string? Description,
    string? ParametersJson,
    bool IsActive,
    DateOnly? EffectiveFrom,
    DateOnly? EffectiveTo,
    string? BusinessProductId,
    string BusinessPlatformName,
    /// <summary>
    /// What the line asks of the household and of the people designated on it (BNF-B-06). Never
    /// null: an unconfigured line reads as the restrictive default — no household, no designation.
    /// </summary>
    BeneficiaryPolicy? BeneficiaryPolicy = null,
    /// <summary>Whether a customer may hold this line more than once (BNF-B-22).</summary>
    bool AllowMultipleHoldings = true,
    /// <summary>
    /// Whether this line may be KYC'd at a distance after a conversion, and how such a file is
    /// validated (KRM-B-01). Never null in practice: an unconfigured line reads as the restrictive
    /// default — remote collection off, manual validation.
    /// </summary>
    KycRemotePolicy? KycRemotePolicy = null,
    /// <summary>Values of the tenant's custom product fields, keyed by field key. Filled on the detail read only.</summary>
    IReadOnlyDictionary<string, System.Text.Json.JsonElement>? CustomFields = null);
