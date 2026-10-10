namespace Sankore.Modules.Administration.Domain;

/// <summary>
/// How the institution is bound by FATCA (BNF-B-10). There is no "none" value: the regime is only
/// read when FATCA is enabled, and a disabled institution has no regime rather than a null one.
/// </summary>
public enum FatcaRegime
{
    /// <summary>
    /// Intergovernmental agreement, model 1 — the institution reports to its own tax authority,
    /// which forwards to the IRS. The common case in the UEMOA zone.
    /// </summary>
    IgaModel1,

    /// <summary>Intergovernmental agreement, model 2 — the institution reports directly to the IRS.</summary>
    IgaModel2,

    /// <summary>No agreement: the institution registered with the IRS as a participating FFI itself.</summary>
    DirectFfiAgreement
}

/// <summary>
/// The point in the commercial journey at which a documented FATCA status becomes mandatory
/// (BNF-B-10).
///
/// <para>
/// Both values are defensible and the choice is the institution's. <see cref="Conversion"/> is the
/// default and the laxer of the two: a prospect is asked only once the relationship is about to
/// become an account. <see cref="Enrichment"/> asks during qualification, which collects better
/// — the agent is already on the phone — at the cost of refusing to complete an enrichment over a
/// form the prospect may not understand yet.
/// </para>
/// </summary>
public enum FatcaRequiredAt
{
    /// <summary>Required before a lead can be converted. The default.</summary>
    Conversion,

    /// <summary>Required before the enrichment of a lead can be considered complete.</summary>
    Enrichment
}
