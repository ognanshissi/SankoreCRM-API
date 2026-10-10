namespace Sankore.Shared.Kernel.ValueObject;

/// <summary>
/// The legal form of a spousal union (BNF-B-03, BNF-B-13). Required for a spouse, forbidden for a
/// child.
///
/// <para>
/// <b>In the Kernel because two modules must agree on it.</b> M13 records it on a
/// <c>LeadHouseholdMember</c> and the conversion copies it onto M01's <c>ClientRelationship</c>; two
/// per-module copies would be two lists that drift, which is exactly what keeping the client
/// household on the existing relationship table is meant to avoid. Same reasoning as
/// <see cref="PersonIdentity"/> and <see cref="PepStatus"/>.
/// </para>
///
/// <para>
/// Deliberately extensible: the UEMOA zone recognises forms this list does not yet carry, and the
/// specification says so ("enum extensible"). Persisted as a string on both sides, so appending a
/// value moves no stored row.
/// </para>
/// </summary>
public enum SpouseUnionType
{
    Married,

    /// <summary>A civil partnership — the PACS of French law and its equivalents.</summary>
    CivilUnion
}
