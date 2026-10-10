namespace Sankore.Shared.Kernel.ValueObject;

/// <summary>
/// Politically-exposed-person status of one natural person (BNF-B-02, BNF-B-05). Mapped by each
/// module as an EF owned type; held by the principal and by every household member.
///
/// <para>
/// <b>Not disablable per tenant, unlike FATCA.</b> PEP vigilance follows the LBC/FT rules that
/// apply to every UEMOA institution, so there is no setting that turns this off. The only tenant
/// parameter it reads is how long exposure outlives the mandate — see
/// <see cref="IsExposedAt"/>.
/// </para>
///
/// <para>
/// <b>Nothing here is encrypted, and the protection is an authorization one.</b> A function title
/// and a country are not identity data, but together they often name the person outright — so
/// reading the detail is gated on the PEP read permission rather than on a key. The exposure
/// FLAG stays readable: dispatching has to route an exposed lead to a cleared agent without
/// anybody being shown why.
/// </para>
/// </summary>
public sealed record PepStatus
{
    public bool IsPep { get; private init; }

    /// <summary>The exposed function. Required when <see cref="IsPep"/>.</summary>
    public string? Function { get; private init; }

    /// <summary>Country of the function. ISO 3166-1 alpha-2, upper-cased.</summary>
    public string? CountryCode { get; private init; }

    /// <summary>
    /// When the function ended, or null while it is still held. Exposure does NOT end here — see
    /// <see cref="IsExposedAt"/>.
    /// </summary>
    public DateOnly? FunctionEndDate { get; private init; }

    /// <summary>How the person is connected to the function. Required when <see cref="IsPep"/>.</summary>
    public PepLink? Link { get; private init; }

    /// <summary>
    /// Why <see cref="Link"/> departs from what the principal's status implies. BNF-B-05 propagates
    /// <see cref="PepLink.FamilyMember"/> onto the household of a <see cref="PepLink.Self"/>
    /// principal by default and allows an agent to override it — but an override of an LBC/FT
    /// classification that carries no reason is one nobody can review later.
    /// </summary>
    public string? OverrideReason { get; private init; }

    private PepStatus() { } // EF Core

    /// <summary>
    /// The default for every person: no declared exposure.
    ///
    /// <para>
    /// <b>A FRESH instance on every access, and that is load-bearing.</b> This type is mapped as an
    /// EF OWNED entity, so an instance is tracked against the row that holds it. A single shared
    /// static would make every unexposed person's status the same object, and the second row to
    /// use it fails the save with <i>"The property 'Lead.PrincipalPep#PepStatus.LeadId' is part of
    /// a key and so cannot be modified"</i> — EF reading the shared reference as the owned entity
    /// being re-parented. Measured, not theorised: it broke the moment two leads were captured
    /// through one DbContext.
    /// </para>
    ///
    /// <para>
    /// Hence an expression-bodied property rather than an initialised one. The allocation is a
    /// record with five null fields; the aliasing it avoids is a save that fails at run time on
    /// the second row.
    /// </para>
    /// </summary>
    public static PepStatus NotExposed => new() { IsPep = false };

    /// <summary>
    /// Declares an exposure.
    /// </summary>
    /// <exception cref="DomainException">
    /// When the function is blank or the link is absent. Both are required by BNF-B-02: an
    /// exposure with neither says only that somebody ticked a box, and the vigilance level it
    /// drives would not be justifiable to a regulator.
    /// </exception>
    public static PepStatus Exposed(
        string function,
        PepLink link,
        string? countryCode = null,
        DateOnly? functionEndDate = null,
        string? overrideReason = null)
    {
        if (string.IsNullOrWhiteSpace(function))
            throw new DomainException(
                "The exposed function is required when a person is declared PEP.",
                "PepStatus.Function.Required");

        var country = IsoCountry.Normalize(countryCode);
        if (country is not null && !IsoCountry.HasValidShape(country))
            throw new DomainException(
                "Country code must be two letters (ISO 3166-1 alpha-2).",
                "PepStatus.CountryCode.Invalid");

        return new PepStatus
        {
            IsPep           = true,
            Function        = function.Trim(),
            Link            = link,
            CountryCode     = country,
            FunctionEndDate = functionEndDate,
            OverrideReason  = string.IsNullOrWhiteSpace(overrideReason) ? null : overrideReason.Trim(),
        };
    }

    /// <summary>
    /// Whether the person still counts as exposed at <paramref name="asOf"/>.
    ///
    /// <para>
    /// <b>The single definition of the post-mandate window</b> (BNF-B-05). A person whose function
    /// ended stays exposed for <paramref name="postMandateMonths"/> — the tenant's
    /// <c>PepPostMandateMonths</c>, 12 by default. It lives on the value object precisely so M13's
    /// completeness check, M13's dispatching and M01's recalculation cannot each implement their
    /// own version of it and disagree about the same person.
    /// </para>
    ///
    /// <para>
    /// A negative <paramref name="postMandateMonths"/> is clamped to zero rather than rejected: a
    /// misconfigured parameter must narrow the window, never invert it into "exposed before the
    /// mandate started".
    /// </para>
    /// </summary>
    public bool IsExposedAt(DateOnly asOf, int postMandateMonths)
    {
        if (!IsPep)
            return false;

        if (FunctionEndDate is not { } ended)
            return true;   // still in office

        return asOf <= ended.AddMonths(Math.Max(0, postMandateMonths));
    }

    /// <summary>
    /// The status a household member inherits from an exposed principal: the same function and
    /// country, re-linked as <see cref="PepLink.FamilyMember"/>. Returns
    /// <see cref="NotExposed"/> when the principal is not exposed, so a caller can apply it
    /// unconditionally.
    /// </summary>
    public PepStatus AsFamilyMemberLink()
        => !IsPep
            ? NotExposed
            : this with { Link = PepLink.FamilyMember, OverrideReason = null };
}
