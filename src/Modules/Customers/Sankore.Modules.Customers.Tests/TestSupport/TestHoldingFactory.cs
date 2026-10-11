namespace Sankore.Modules.Customers.Tests.TestSupport;

using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Kernel;

/// <summary>
/// Builders for the holdings and designations the BNF-B-14/15 tests need over and over.
/// </summary>
public static class TestHoldingFactory
{
    /// <summary>
    /// A product line as M12's contract hands it over. The roles default to a death-benefit clause
    /// that MUST total 100% across one to four beneficiaries — the shape every share rule is about.
    /// </summary>
    public static BeneficiaryPolicyDto Line(
        Guid? productLineId = null,
        string code = "VIE-01",
        string category = nameof(ProductCategory.Insurance),
        bool allowMultipleHoldings = true,
        IReadOnlyList<DesignationRoleDto>? roles = null,
        string? requiredKycTier = null,
        string? integrationSystem = null)
        => new(
            ProductLineId: productLineId ?? Guid.NewGuid(),
            ProductCode: code,
            Category: category,
            IsActive: true,
            AllowMultipleHoldings: allowMultipleHoldings,
            AllowedRoles: roles ?? [DeathBenefit],
            MaxChildren: null,
            ChildMaxAge: null,
            RequiredFieldsBeforeConversion: [],
            RequiredKycTier: requiredKycTier,
            IntegrationSystem: integrationSystem);

    /// <summary>One to four beneficiaries whose shares must total 100% within a rank.</summary>
    public static DesignationRoleDto DeathBenefit { get; } =
        new("BENEFICIAIREDECES", "Bénéficiaire en cas de décès", MinCount: 1, MaxCount: 4, SharesMustTotal100: true);

    /// <summary>A role where a share means nothing: optional, uncapped, no total.</summary>
    public static DesignationRoleDto Proxy { get; } =
        new("MANDATAIRE", "Mandataire", MinCount: 0, MaxCount: null, SharesMustTotal100: false);

    /// <summary>A draft CRM subscription, ready to be designated on.</summary>
    public static ProductHolding Draft(
        Guid tenantId,
        Guid clientId,
        BeneficiaryPolicyDto line,
        Guid? createdBy = null,
        DateTimeOffset? now = null,
        HoldingSpecifics? specifics = null)
        => ProductHolding.Subscribe(
            tenantId: tenantId,
            clientId: clientId,
            line: line,
            specifics: specifics ?? HoldingSpecifics.None,
            createdBy: createdBy ?? Guid.NewGuid(),
            now: now ?? DateTimeOffset.UtcNow);

    /// <summary>
    /// One designation request. Defaults to an external person, because that is the only subject
    /// type a test can use without seeding a client or a relationship first.
    /// </summary>
    public static DesignationRequest Beneficiary(
        decimal? share,
        string roleCode = "BENEFICIAIREDECES",
        int rank = 1,
        string name = "Awa Ouattara",
        DesignationSubjectType subjectType = DesignationSubjectType.ExternalPerson,
        Guid? subjectId = null)
        => new(
            RoleCode: roleCode,
            SubjectType: subjectType,
            SubjectId: subjectId,
            ExternalFullName: subjectType == DesignationSubjectType.ExternalPerson ? name : null,
            ExternalRelationship: subjectType == DesignationSubjectType.ExternalPerson ? "Épouse" : null,
            Rank: rank,
            SharePercent: share);
}
