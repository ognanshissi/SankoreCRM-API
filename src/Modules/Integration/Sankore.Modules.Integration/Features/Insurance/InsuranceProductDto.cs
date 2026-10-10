namespace Sankore.Modules.Integration.Features.Insurance;

using System.Text.Json;
using System.Text.Json.Serialization;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// How a product's guarantee list crosses the <c>jsonb</c> boundary.
///
/// <para>
/// Camel-cased and enum-as-name, like every other serialised shape in this repo, and
/// <b>case-insensitive on read</b> for the reason <c>IntegrationModuleFacade.SnapshotJson</c>
/// gives: a column whose writer and reader disagree on casing does not fail, it returns an empty
/// list — a product that silently shows no guarantees at the counter, with nothing in the logs.
/// </para>
/// </summary>
internal static class InsuranceGuaranteeCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    internal static string Serialise(IReadOnlyList<InsuranceGuarantee>? guarantees)
        => JsonSerializer.Serialize(guarantees ?? [], Options);

    /// <summary>
    /// Reads the column. An unreadable value answers an EMPTY list and never throws: a product
    /// written by an older shape must still be listable, deactivatable and correctable, and an
    /// exception here would take the whole catalogue screen down with one bad row.
    /// </summary>
    internal static IReadOnlyList<InsuranceGuarantee> Deserialise(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            return JsonSerializer.Deserialize<List<InsuranceGuarantee>>(json, Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>
/// One catalogue entry as the administration screen reads it (ASS-03).
///
/// <para>
/// <see cref="IsOfferable"/> is the FULL verdict — including the insurer-pricing capability, which
/// no SQL predicate can decide — and <see cref="NotOfferableReasons"/> says why. A caller decides
/// whether to offer the product on this field and never on having passed <c>offerableOnly</c> to
/// the list; see <see cref="ProductOfferability.IsOfferableInDatabase"/> for why the two differ.
/// </para>
/// </summary>
public sealed record InsuranceProductDto(
    Guid Id,
    Guid ConnectionId,
    string ConnectionName,
    IntegrationKind ConnectionKind,
    string InsurerProductCode,
    string Name,
    string? Description,
    IReadOnlyList<InsuranceGuarantee> Guarantees,
    PremiumPeriodicity Periodicity,
    ProductPricingMode PricingMode,
    decimal? FixedPremiumAmount,
    string? Currency,
    decimal? InsuredAmount,
    int? MinAge,
    int? MaxAge,
    KycLevel MinKycLevel,
    bool RequiresCbsAccount,
    bool RequiresActiveLoan,
    string? LinkedCreditProductCode,
    string? CrmProductCode,
    decimal CommissionRate,
    int PremiumRetryLimit,
    int PremiumRetryIntervalDays,
    bool IsActive,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    bool IsOfferable,
    IReadOnlyList<string> NotOfferableReasons,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <param name="crmCatalogue">
    /// Where the product's <c>CrmProductCode</c> stands in M12's catalogue. A parameter and not a
    /// lookup inside this method: resolving it crosses a module boundary and is therefore async,
    /// while a DTO projection is not — callers resolve it first (one call, or
    /// <c>CrmProductCatalogue.PrefetchAsync</c> for a page) exactly as they already do for
    /// <paramref name="insurerPricingAvailable"/>.
    /// </param>
    internal static InsuranceProductDto From(
        InsuranceProduct product,
        IntegrationConnection connection,
        bool insurerPricingAvailable,
        CrmCatalogueStatus crmCatalogue,
        DateOnly today)
    {
        var reasons = ProductOfferability.Reasons(
            productIsActive: product.IsActive,
            connectionIsActive: connection.IsActive,
            connectionIsInsuranceFamily: connection.Family == IntegrationFamily.Insurance,
            effectiveFrom: product.EffectiveFrom,
            effectiveTo: product.EffectiveTo,
            pricingMode: product.PricingMode,
            insurerPricingAvailable: insurerPricingAvailable,
            crmCatalogue: crmCatalogue,
            today: today);

        return new InsuranceProductDto(
            Id: product.Id,
            ConnectionId: product.ConnectionId,
            ConnectionName: connection.Name,
            ConnectionKind: connection.Kind,
            InsurerProductCode: product.InsurerProductCode,
            Name: product.Name,
            Description: product.Description,
            Guarantees: InsuranceGuaranteeCodec.Deserialise(product.GuaranteesJson),
            Periodicity: product.Periodicity,
            PricingMode: product.PricingMode,
            FixedPremiumAmount: product.FixedPremiumAmount,
            Currency: product.Currency,
            InsuredAmount: product.InsuredAmount,
            MinAge: product.MinAge,
            MaxAge: product.MaxAge,
            MinKycLevel: product.MinKycLevel,
            RequiresCbsAccount: product.RequiresCbsAccount,
            RequiresActiveLoan: product.RequiresActiveLoan,
            LinkedCreditProductCode: product.LinkedCreditProductCode,
            CrmProductCode: product.CrmProductCode,
            CommissionRate: product.CommissionRate,
            PremiumRetryLimit: product.PremiumRetryLimit,
            PremiumRetryIntervalDays: product.PremiumRetryIntervalDays,
            IsActive: product.IsActive,
            EffectiveFrom: product.EffectiveFrom,
            EffectiveTo: product.EffectiveTo,
            IsOfferable: reasons.Count == 0,
            NotOfferableReasons: reasons,
            CreatedAt: product.CreatedAt,
            UpdatedAt: product.UpdatedAt);
    }
}

/// <summary>
/// The body shared by the create and the update of a product. One record, so the two cannot
/// accept different fields — the drift that leaves a column editable on creation only.
/// </summary>
public sealed record InsuranceProductWriteRequest(
    string Name,
    PremiumPeriodicity Periodicity,
    ProductPricingMode PricingMode,
    DateOnly EffectiveFrom,
    string? Description = null,
    IReadOnlyList<InsuranceGuarantee>? Guarantees = null,
    decimal? FixedPremiumAmount = null,
    string? Currency = null,
    decimal? InsuredAmount = null,
    int? MinAge = null,
    int? MaxAge = null,
    KycLevel MinKycLevel = KycLevel.None,
    bool RequiresCbsAccount = false,
    bool RequiresActiveLoan = false,
    string? LinkedCreditProductCode = null,
    string? CrmProductCode = null,
    decimal CommissionRate = 0m,
    int PremiumRetryLimit = 3,
    int PremiumRetryIntervalDays = 3,
    DateOnly? EffectiveTo = null);
