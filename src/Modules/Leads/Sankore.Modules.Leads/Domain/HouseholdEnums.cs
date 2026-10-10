namespace Sankore.Modules.Leads.Domain;

/// <summary>
/// Who a household member is to the principal (BNF-B-03).
///
/// <para>
/// Only two values, and that is the specification's decision rather than a simplification: "un
/// conjoint au plus (marié ou pacsé), zéro à N enfants". A dependent who is neither — a parent, a
/// sibling — is not part of the household this evolution models, and inventing a value for them
/// would make the one-spouse invariant and the child-age ceiling ambiguous.
/// </para>
/// </summary>
public enum HouseholdMemberType
{
    Spouse,
    Child
}

// SpouseUnionType used to live here. It moved to Sankore.Shared.Kernel.ValueObject when M01 needed
// the same list for a client's household (BNF-B-13): the conversion copies the value across, and
// two per-module enums would be two lists that drift.
