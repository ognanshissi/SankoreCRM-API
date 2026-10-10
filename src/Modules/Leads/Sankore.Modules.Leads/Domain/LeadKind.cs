namespace Sankore.Modules.Leads.Domain;

/// <summary>
/// Whether a lead is a brand-new prospect or a request from a customer M01 already holds
/// (BNF-B-22).
///
/// <para>
/// <b>Derived, never stored.</b> It is a reading of <c>Lead.CustomerId</c>, so there is no column
/// and no setter: a stored copy would be a second source of truth for one fact, and the pair could
/// then disagree — a lead marked <see cref="ExistingCustomer"/> with no customer id, or the
/// reverse. Both would be silent, and the conversion branches on this value.
/// </para>
/// </summary>
public enum LeadKind
{
    /// <summary>No matching customer. Conversion creates the client and the KYC file (BNF-B-12).</summary>
    NewProspect,

    /// <summary>
    /// Attached to an existing client. Conversion creates neither client nor KYC file — it opens a
    /// product holding on the client already there (BNF-B-24).
    /// </summary>
    ExistingCustomer
}
