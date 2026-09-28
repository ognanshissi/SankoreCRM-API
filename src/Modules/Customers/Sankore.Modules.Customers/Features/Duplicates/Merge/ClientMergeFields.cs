namespace Sankore.Modules.Customers.Features.Duplicates.Merge;

/// <summary>
/// The field names a merge request may arbitrate, and the two values a choice can take.
/// <para>
/// <c>FieldChoices</c> is a free-form <c>field -&gt; "survivor" | "absorbed"</c> dictionary coming
/// from the review screen. Anything not listed here is ignored SILENTLY: the front-end evolves
/// faster than the back-end, and an unknown key must never turn a legitimate merge into a 400.
/// A known key whose value is not <see cref="Absorbed"/> simply keeps the survivor's value, which
/// is also the default when the key is absent.
/// </para>
/// </summary>
public static class ClientMergeFields
{
    public const string Survivor = "survivor";
    public const string Absorbed = "absorbed";

    // ── Individual identity ─────────────────────────────────────────────────
    public const string FirstName = "FirstName";
    public const string LastName = "LastName";
    public const string MaidenName = "MaidenName";
    public const string Gender = "Gender";
    public const string DateOfBirth = "DateOfBirth";
    public const string BirthPlace = "BirthPlace";
    public const string Nationality = "Nationality";
    public const string MaritalStatus = "MaritalStatus";
    public const string FatherName = "FatherName";
    public const string MotherName = "MotherName";

    // ── Socio-professional ──────────────────────────────────────────────────
    public const string Profession = "Profession";
    public const string Employer = "Employer";
    public const string DeclaredIncome = "DeclaredIncome";
    public const string PreferredLanguage = "PreferredLanguage";

    /// <summary>Type, encrypted number, blind index and both dates move together — they are one document.</summary>
    public const string IdentityDocument = "IdentityDocument";

    // ── Legal entity ────────────────────────────────────────────────────────
    public const string LegalName = "LegalName";
    public const string LegalFormCode = "LegalFormCode";
    public const string RegistrationNumber = "RegistrationNumber";
    public const string TaxIdNumber = "TaxIdNumber";
    public const string IncorporationDate = "IncorporationDate";

    // ── Ownership ───────────────────────────────────────────────────────────
    public const string AdvisorUserId = "AdvisorUserId";

    /// <summary>Every arbitrable field, for validation and documentation.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        FirstName, LastName, MaidenName, Gender, DateOfBirth, BirthPlace, Nationality,
        MaritalStatus, FatherName, MotherName, Profession, Employer, DeclaredIncome,
        PreferredLanguage, IdentityDocument, LegalName, LegalFormCode, RegistrationNumber,
        TaxIdNumber, IncorporationDate, AdvisorUserId,
    };
}
