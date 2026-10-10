namespace Sankore.Shared.Kernel.ValueObject;

/// <summary>
/// Identity of one natural person — the lead's principal, a spouse, a child, a beneficiary
/// (BNF-B-02). Mapped by each module as an EF owned type into its own schema; the Kernel owns the
/// shape and the normalisation so the four places that need a person cannot drift apart.
///
/// <para>
/// <b>Which fields are sensitive.</b> <see cref="BirthDate"/> and <see cref="AddressLine"/> are
/// encrypted at rest by every module that maps this type, and the audit trail records their field
/// NAMES only. The names are deliberately NOT encrypted: prefix search over a surname has to stay
/// an indexable <c>LIKE 'TERM%'</c>, which is the same trade-off M01 already made for
/// <c>Client.SearchKey</c>.
/// </para>
///
/// <para>
/// <b>Plaintext in the domain, ciphertext in the column.</b> This type holds clear values; the
/// encryption is a persistence concern applied by the mapping (see
/// <c>PersonIdentityMapping.OwnsPersonIdentity</c> in Shared.Infrastructure). Handlers therefore
/// cannot forget to encrypt — which is how M01's <c>EncryptedDateOfBirth</c> style of field puts
/// the burden on every caller instead.
/// </para>
///
/// <para>
/// <b>Why the two names are required.</b> Every other field is optional, because BNF-B-01 wants a
/// lead capturable from almost nothing. But an owned type whose every column is null is one EF
/// cannot tell from an absent owner, and a household member with no name is not a person anybody
/// can act on. Callers holding a partially-known person use <c>PersonIdentity?</c> and leave it
/// null — see the principal's projection on the Lead aggregate.
/// </para>
/// </summary>
public sealed record PersonIdentity
{
    public string LastName { get; private init; } = default!;
    public string FirstName { get; private init; } = default!;

    /// <summary>Name the person is known by when it differs from <see cref="LastName"/> (nom d'usage).</summary>
    public string? UsageName { get; private init; }

    /// <summary>SENSITIVE — encrypted at rest.</summary>
    public DateOnly? BirthDate { get; private init; }

    public PersonGender Gender { get; private init; }

    /// <summary>SENSITIVE — encrypted at rest. Street and number, one line.</summary>
    public string? AddressLine { get; private init; }

    public string? PostalCode { get; private init; }
    public string? City { get; private init; }

    /// <summary>ISO 3166-1 alpha-2, upper-cased. See <see cref="IsoCountry"/>.</summary>
    public string? CountryCode { get; private init; }

    /// <summary>Phase 2, France. Always empty in phase 1 — see <see cref="FrenchSocialSecurity"/>.</summary>
    public FrenchSocialSecurity? FrenchSocialSecurity { get; private init; }

    private PersonIdentity() { } // EF Core

    /// <summary>
    /// Builds a normalised identity: names trimmed, blank optionals collapsed to null, country code
    /// upper-cased.
    /// </summary>
    /// <exception cref="DomainException">
    /// When either name is blank, or when <paramref name="countryCode"/> is present but is not two
    /// ASCII letters. Whether those two letters name a real country is a validator's question —
    /// <see cref="IsoCountry.IsKnown"/> — not this constructor's: a value object must not fail on a
    /// country the host's globalization data happens not to carry.
    /// </exception>
    public static PersonIdentity Create(
        string lastName,
        string firstName,
        string? usageName = null,
        DateOnly? birthDate = null,
        PersonGender gender = PersonGender.Unknown,
        string? addressLine = null,
        string? postalCode = null,
        string? city = null,
        string? countryCode = null,
        FrenchSocialSecurity? frenchSocialSecurity = null)
    {
        if (string.IsNullOrWhiteSpace(lastName))
            throw new DomainException("Last name is required.", "PersonIdentity.LastName.Required");
        if (string.IsNullOrWhiteSpace(firstName))
            throw new DomainException("First name is required.", "PersonIdentity.FirstName.Required");

        var country = IsoCountry.Normalize(countryCode);
        if (country is not null && !IsoCountry.HasValidShape(country))
            throw new DomainException(
                "Country code must be two letters (ISO 3166-1 alpha-2).",
                "PersonIdentity.CountryCode.Invalid");

        return new PersonIdentity
        {
            LastName    = lastName.Trim(),
            FirstName   = firstName.Trim(),
            UsageName   = Blank(usageName),
            BirthDate   = birthDate,
            Gender      = gender,
            AddressLine = Blank(addressLine),
            PostalCode  = Blank(postalCode),
            City        = Blank(city),
            CountryCode = country,
            FrenchSocialSecurity =
                frenchSocialSecurity is { IsEmpty: false } ? frenchSocialSecurity : null,
        };
    }

    /// <summary>
    /// "Prénom Nom" — the display order of the UEMOA front-ends. Not a storage key: M01 keys its
    /// prefix search on <c>SearchKey</c>, which is surname-first, upper-case and accent-free.
    /// </summary>
    public string FullName => $"{FirstName} {LastName}";

    /// <summary>
    /// The same identity with the postal address replaced — how a household member that inherits
    /// the principal's address is resolved AT READ TIME. The inheritance is a link, never a copy:
    /// <c>UsesPrincipalAddress</c> stays on the member and the principal's address is applied here,
    /// so moving the principal moves the whole household.
    /// </summary>
    public PersonIdentity WithAddressOf(PersonIdentity principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        return this with
        {
            AddressLine = principal.AddressLine,
            PostalCode  = principal.PostalCode,
            City        = principal.City,
            CountryCode = principal.CountryCode,
        };
    }

    /// <summary>
    /// The same identity with the postal address dropped — what a member that INHERITS the
    /// principal's address stores.
    ///
    /// <para>
    /// The counterpart of <see cref="WithAddressOf"/>, and it exists so the inheritance is
    /// enforced on WRITE rather than only honoured on read. A member whose own postal fields were
    /// merely ignored keeps a stale address in its row, and the day somebody turns inheritance off
    /// that address comes back — silently, and years out of date.
    /// </para>
    ///
    /// <para>
    /// <see cref="CountryCode"/> is KEPT: it is part of who the person is for reporting and for
    /// the French-social-security gate, and a household member living at the principal's address
    /// is not thereby of the principal's nationality.
    /// </para>
    /// </summary>
    public PersonIdentity WithoutPostalAddress() => this with
    {
        AddressLine = null,
        PostalCode  = null,
        City        = null,
    };

    /// <summary>
    /// Age in whole years at <paramref name="asOf"/>, or null when the birth date is unknown.
    /// Used by the child-age ceiling of a product line's beneficiary policy.
    /// </summary>
    public int? AgeAt(DateOnly asOf)
    {
        if (BirthDate is not { } born)
            return null;

        var age = asOf.Year - born.Year;
        if (asOf < born.AddYears(age))
            age--;

        return age;
    }

    /// <summary>
    /// Blanks every sensitive field, keeping the row and its links intact (BNF-B-19). Names are
    /// replaced rather than emptied: the two are required, and a reader must be able to tell an
    /// anonymised person from a half-filled one.
    /// </summary>
    public PersonIdentity Anonymized() => new()
    {
        LastName    = "ANONYMIZED",
        FirstName   = "ANONYMIZED",
        UsageName   = null,
        BirthDate   = null,
        Gender      = PersonGender.Unknown,
        AddressLine = null,
        PostalCode  = null,
        City        = null,
        CountryCode = CountryCode,  // kept: a country is not nominative and reporting needs it
        FrenchSocialSecurity = null,
    };

    private static string? Blank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
