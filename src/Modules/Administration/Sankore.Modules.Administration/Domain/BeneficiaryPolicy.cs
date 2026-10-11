namespace Sankore.Modules.Administration.Domain;

using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;

/// <summary>
/// What a product line asks of a customer's household and of the people designated on it
/// (BNF-B-06). Stored as a typed <c>jsonb</c> column on <see cref="ProductSpeciality"/> and read by
/// M13 (a lead's completeness and the household gate) and M01 (designations).
///
/// <para>
/// <b>Why it lives on the product and not in a settings table.</b> Every value here answers "for
/// THIS product": a health insurance needs a household and a beneficiary per insured person, a
/// current account needs neither. A per-tenant setting could not express that, and a per-category
/// one would force two insurance products to share a policy they may well not share.
/// </para>
///
/// <para>
/// <b>Why a record and not columns.</b> Optional, product-shaped fields that M13 and M01 read
/// whole and never query individually. Columns would be as many migrations' worth of nullable fields
/// nobody filters on; the exception is <c>AllowMultipleHoldings</c>, which sits on the entity as a
/// real column precisely because it IS queried — see <see cref="ProductSpeciality.AllowMultipleHoldings"/>.
/// </para>
/// </summary>
/// <param name="AllowedRoles">
/// The designation roles this line accepts, with their cardinality and share rules. Empty means
/// the line designates nobody — a current account with no beneficiary clause.
/// </param>
/// <param name="MaxChildren">Null = no ceiling.</param>
/// <param name="ChildMaxAge">
/// Age above which a child may no longer be carried on this line, in whole years at the date of
/// the check. Null = no limit.
/// </param>
/// <param name="RequiredFieldsBeforeConversion">
/// Stable codes of the fields a lead must carry before it can convert (BNF-B-04). Codes, not
/// labels: the front-end maps them to its own wording, and the completeness response is a list of
/// what is missing per person.
/// </param>
/// <param name="RequiredKycTier">
/// The KYC level this line requires — "Simplified" or "Full". Null = none required beyond the
/// tenant's own baseline. A string rather than M02's enum: M12 may not reference another module,
/// and the value is compared by name at the gate.
/// </param>
/// <param name="IntegrationSystem">
/// Which back-office receives a subscription on this line (BNF-B-14/16) — an
/// <c>IntegrationKind</c> name such as "Temenos" or "Orass". Null = the line is not pushed
/// anywhere, and the holding stays CRM-only.
/// </param>
public sealed record BeneficiaryPolicy(
    IReadOnlyList<DesignationRoleDefinition>? AllowedRoles = null,
    int? MaxChildren = null,
    int? ChildMaxAge = null,
    IReadOnlyList<string>? RequiredFieldsBeforeConversion = null,
    string? RequiredKycTier = null,
    string? IntegrationSystem = null)
{
    /// <summary>
    /// The policy a product line has until an administrator configures one: no designation role,
    /// no ceiling, nothing required. The most RESTRICTIVE reading of "unconfigured" for everything
    /// it still carries — a line nobody has set up designates nobody.
    /// <para>
    /// It no longer says anything about the household. A <c>HouseholdEnabled</c> flag lived here
    /// and defaulted to false, which made this "restrictive" default refuse a spouse on every
    /// unconfigured line — see <see cref="Create"/>.
    /// </para>
    /// </summary>
    public static BeneficiaryPolicy None { get; } = new();

    public IReadOnlyList<DesignationRoleDefinition> Roles => AllowedRoles ?? [];

    public IReadOnlyList<string> RequiredFields => RequiredFieldsBeforeConversion ?? [];

    /// <summary>The role definition for <paramref name="code"/>, or null when the line refuses it.</summary>
    public DesignationRoleDefinition? Role(string code)
        => Roles.FirstOrDefault(r => string.Equals(r.Code, code, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Validates and normalises a policy an administrator submitted.
    /// </summary>
    /// <returns>
    /// <c>BENEFICIARY_POLICY_DUPLICATE_ROLE</c>, <c>BENEFICIARY_POLICY_INVALID_CHILD_LIMIT</c>,
    /// <c>BENEFICIARY_POLICY_UNKNOWN_REQUIRED_FIELD:&lt;code&gt;</c>, or one of
    /// <see cref="DesignationRoleDefinition"/>'s codes.
    /// </returns>
    public static Result<BeneficiaryPolicy> Create(
        IReadOnlyList<DesignationRoleDefinition>? allowedRoles,
        int? maxChildren,
        int? childMaxAge,
        IReadOnlyList<string>? requiredFieldsBeforeConversion,
        string? requiredKycTier,
        string? integrationSystem)
    {
        var roles = allowedRoles ?? [];

        foreach (var role in roles)
        {
            var valid = role.Validate();
            if (valid.IsFailure)
                return Result.Fail<BeneficiaryPolicy>(valid.Error!);
        }

        // Case-insensitively: two roles differing only in casing are one role as far as every
        // lookup here is concerned, so accepting both would make Role() return whichever came first.
        var codes = roles.Select(r => r.Code.Trim().ToUpperInvariant()).ToList();
        if (codes.Count != codes.Distinct(StringComparer.Ordinal).Count())
            return Result.Fail<BeneficiaryPolicy>("BENEFICIARY_POLICY_DUPLICATE_ROLE");

        if (maxChildren is < 0 || childMaxAge is < 0 or > 130)
            return Result.Fail<BeneficiaryPolicy>("BENEFICIARY_POLICY_INVALID_CHILD_LIMIT");

        // Every required field must be a code something can EVALUATE (PersonFieldCode). Refused
        // here rather than ignored by the consumer: a free-text list meant a typo was a
        // requirement that silently never applied, which is the one outcome a conversion gate must
        // not have. The offending code is named so an administrator can see which it was.
        var unknown = (requiredFieldsBeforeConversion ?? [])
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .FirstOrDefault(f => !PersonFieldCode.IsKnown(f));

        if (unknown is not null)
            return Result.Fail<BeneficiaryPolicy>(
                $"BENEFICIARY_POLICY_UNKNOWN_REQUIRED_FIELD:{unknown.Trim()}");

        // The ceilings are stored as submitted. They used to be discarded unless a now-removed
        // `householdEnabled` flag was set — and since that flag defaulted to false, an
        // administrator who typed "at most 3 children" without ticking a separate box had the
        // limit silently thrown away. A household is recordable on every line, so a ceiling always
        // means something.
        return Result.Ok(new BeneficiaryPolicy(
            AllowedRoles: [.. roles.Select(r => r.Normalized())],
            MaxChildren: maxChildren,
            ChildMaxAge: childMaxAge,
            // Normalised through PersonFieldCode, so a stored policy holds one spelling per field
            // and M13's lookup never has to care how an administrator typed it.
            RequiredFieldsBeforeConversion:
                [.. (requiredFieldsBeforeConversion ?? [])
                    .Select(PersonFieldCode.Normalize)
                    .Where(f => f is not null)
                    .Select(f => f!)
                    .Distinct(StringComparer.Ordinal)],
            RequiredKycTier: Blank(requiredKycTier),
            IntegrationSystem: Blank(integrationSystem)));
    }

    private static string? Blank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// One designation role a product line accepts, with the cardinality and share rules that govern
/// it (BNF-B-06, BNF-B-15).
///
/// <para>
/// Roles are per-line STRINGS rather than a platform enum — "AyantDroitSante",
/// "BeneficiaireDeces", "CoTitulaire", "Mandataire" are the examples in the specification, and the
/// list is explicitly the administrator's to set. An enum would mean a deploy every time an
/// institution invents a role, which is exactly what "configurables par ligne" rules out.
/// </para>
/// </summary>
/// <param name="Code">Stable identifier, upper-cased on write. What a designation stores.</param>
/// <param name="Label">What an agent reads. The institution's own wording, in its own language.</param>
/// <param name="MinCount">Designations of this role required before the holding can be signed. 0 = optional.</param>
/// <param name="MaxCount">Null = unlimited.</param>
/// <param name="SharesMustTotal100">
/// Whether the quote-parts of this role must sum to exactly 100% within a rank (BNF-B-15). True for
/// a death benefit, where the capital has to be fully allocated; false for a co-holder or a proxy,
/// where a share means nothing.
/// </param>
public sealed record DesignationRoleDefinition(
    string Code,
    string Label,
    int MinCount = 0,
    int? MaxCount = null,
    bool SharesMustTotal100 = false)
{
    internal Result Validate()
    {
        if (string.IsNullOrWhiteSpace(Code))
            return Result.Fail("DESIGNATION_ROLE_CODE_REQUIRED");
        if (Code.Trim().Length > 50)
            return Result.Fail("DESIGNATION_ROLE_CODE_TOO_LONG");
        if (string.IsNullOrWhiteSpace(Label))
            return Result.Fail("DESIGNATION_ROLE_LABEL_REQUIRED");
        if (MinCount < 0)
            return Result.Fail("DESIGNATION_ROLE_INVALID_CARDINALITY");
        if (MaxCount is { } max && (max < 1 || max < MinCount))
            return Result.Fail("DESIGNATION_ROLE_INVALID_CARDINALITY");

        // A single designation holding 100% is the only way the shares of a one-slot role can add
        // up, which makes the rule vacuous rather than wrong — but it also means an administrator
        // ticked a box that does nothing, and that is worth saying out loud.
        if (SharesMustTotal100 && MaxCount == 1 && MinCount == 1)
            return Result.Fail("DESIGNATION_ROLE_SHARE_RULE_POINTLESS");

        return Result.Ok();
    }

    internal DesignationRoleDefinition Normalized() => this with
    {
        Code = Code.Trim().ToUpperInvariant(),
        Label = Label.Trim(),
    };
}
