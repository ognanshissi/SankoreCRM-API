namespace Sankore.Shared.Kernel.ValueObject;

/// <summary>
/// Which people a required field can be asked of.
/// </summary>
public enum PersonFieldScope
{
    /// <summary>
    /// Only the principal has one. A spouse's own telephone number or desired amount is not
    /// something this platform collects, so requiring it of the household would produce a gap
    /// nobody can close.
    /// </summary>
    PrincipalOnly,

    /// <summary>The principal and every active household member.</summary>
    EveryPerson,
}

/// <summary>
/// The closed set of field codes a product line may require before conversion (BNF-B-04/B-06) —
/// <c>BeneficiaryPolicy.RequiredFieldsBeforeConversion</c>.
///
/// <para>
/// <b>Why the list is in the Kernel.</b> Two modules have to agree on it and they are not allowed
/// to reference each other: M12 stores the codes and must refuse one it does not recognise, M13
/// evaluates them against a lead and its household. Before this existed the codes were free text,
/// which meant a typo in a product's configuration was a requirement that silently never applied —
/// the worst possible failure for a list whose whole purpose is to gate a conversion.
/// </para>
///
/// <para>
/// <b>Closed and not free text, deliberately.</b> An institution cannot invent a code here the way
/// it invents a designation role, because a code is only meaningful if something evaluates it.
/// Adding one is a code change in two places — which is the honest cost, and much cheaper than a
/// requirement that reads as configured and enforces nothing.
/// </para>
/// </summary>
public static class PersonFieldCode
{
    public const string LastName   = "LAST_NAME";
    public const string FirstName  = "FIRST_NAME";
    public const string UsageName  = "USAGE_NAME";
    public const string BirthDate  = "BIRTH_DATE";
    public const string Gender     = "GENDER";
    public const string Address    = "ADDRESS";
    public const string PostalCode = "POSTAL_CODE";
    public const string City       = "CITY";
    public const string Country    = "COUNTRY";

    /// <summary>The principal's telephone number — the one field a lead always carries.</summary>
    public const string Phone = "PHONE";

    public const string Email      = "EMAIL";
    public const string NationalId = "NATIONAL_ID";

    /// <summary>The amount the request is about. Meaningless for a household member.</summary>
    public const string DesiredAmount = "DESIRED_AMOUNT";

    /// <summary>
    /// A PEP status having been DECLARED — exposed or not. Satisfied by an explicit negative
    /// answer, never by the absence of one.
    /// </summary>
    public const string PepStatus = "PEP_STATUS";

    // ── Consents a product line may require before conversion ────────────
    //
    // Codes like the others rather than a separate list on the policy: they gate the same
    // conversion, through the same calculator, and an administrator configures them in the same
    // place. Lead-wide, so principal-only — a household member does not consent to the
    // processing of the file, the prospect does. Health consent is NOT here: it is per person and
    // already required by the questionnaires that need it (BNF-B-09).

    /// <summary>Consent to process the personal data the request needs.</summary>
    public const string ConsentDataProcessing = "CONSENT_DATA_PROCESSING";

    /// <summary>Consent to consult the credit bureau (Bureau d'Information sur le Crédit).</summary>
    public const string ConsentCreditBureau = "CONSENT_CREDIT_BUREAU";

    /// <summary>Consent to share the file with a partner — an insurer for borrower insurance.</summary>
    public const string ConsentThirdPartySharing = "CONSENT_THIRD_PARTY_SHARING";

    /// <summary>Every code, with the people it can be asked of.</summary>
    public static IReadOnlyDictionary<string, PersonFieldScope> All { get; } =
        new Dictionary<string, PersonFieldScope>(StringComparer.OrdinalIgnoreCase)
        {
            [LastName]      = PersonFieldScope.EveryPerson,
            [FirstName]     = PersonFieldScope.EveryPerson,
            [UsageName]     = PersonFieldScope.EveryPerson,
            [BirthDate]     = PersonFieldScope.EveryPerson,
            [Gender]        = PersonFieldScope.EveryPerson,
            [Address]       = PersonFieldScope.EveryPerson,
            [PostalCode]    = PersonFieldScope.EveryPerson,
            [City]          = PersonFieldScope.EveryPerson,
            [Country]       = PersonFieldScope.EveryPerson,
            [PepStatus]     = PersonFieldScope.EveryPerson,

            [Phone]         = PersonFieldScope.PrincipalOnly,
            [Email]         = PersonFieldScope.PrincipalOnly,
            [NationalId]    = PersonFieldScope.PrincipalOnly,
            [DesiredAmount] = PersonFieldScope.PrincipalOnly,

            [ConsentDataProcessing]    = PersonFieldScope.PrincipalOnly,
            [ConsentCreditBureau]      = PersonFieldScope.PrincipalOnly,
            [ConsentThirdPartySharing] = PersonFieldScope.PrincipalOnly,
        };

    /// <summary>Whether <paramref name="code"/> names a required consent rather than a field.</summary>
    public static bool IsConsent(string code)
        => code.StartsWith("CONSENT_", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="code"/> is one this platform can evaluate. Case-insensitive, and it
    /// trims — an administrator pasting a list gets whitespace for free.
    /// </summary>
    public static bool IsKnown(string? code)
        => !string.IsNullOrWhiteSpace(code) && All.ContainsKey(code.Trim());

    /// <summary>
    /// Normalised form: trimmed and upper-cased, or null when unknown. What a store should keep,
    /// so a lookup never has to care how it was typed.
    /// </summary>
    public static string? Normalize(string? code)
        => IsKnown(code) ? code!.Trim().ToUpperInvariant() : null;

    public static PersonFieldScope ScopeOf(string code)
        => All.TryGetValue(code.Trim(), out var scope) ? scope : PersonFieldScope.PrincipalOnly;
}
