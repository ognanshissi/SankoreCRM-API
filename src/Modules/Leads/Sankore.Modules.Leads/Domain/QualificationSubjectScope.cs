namespace Sankore.Modules.Leads.Domain;

/// <summary>
/// Who a qualification questionnaire is answered ABOUT (BNF-B-07).
///
/// <para>
/// <see cref="Lead"/> is the default and the pre-existing behaviour: one response per lead, which
/// is what BANT and every template written before this evolution expects. Everything else produces
/// one response PER PERSON, which is the whole point — the same health questionnaire has to be
/// answered for the principal, the spouse and each child.
/// </para>
/// </summary>
public enum QualificationSubjectScope
{
    /// <summary>
    /// One response for the whole lead. The default, and what every existing template keeps —
    /// BNF-B-07 requires their behaviour to be unchanged.
    /// </summary>
    Lead,

    /// <summary>The principal and every active household member.</summary>
    EachInsuredPerson,

    /// <summary>The principal only, but as a PERSON — distinct from <see cref="Lead"/>.</summary>
    PrincipalOnly,

    /// <summary>The active spouse, if there is one.</summary>
    SpouseOnly,

    /// <summary>Every active child.</summary>
    ChildrenOnly
}

/// <summary>Whose answers a <see cref="QualificationResponse"/> records (BNF-B-08).</summary>
public enum QualificationSubjectType
{
    /// <summary>
    /// The lead as a whole — a <see cref="QualificationSubjectScope.Lead"/> questionnaire. The
    /// response's <c>SubjectId</c> is then the lead's own id, so the unique constraint has a value
    /// to work with rather than a null that PostgreSQL would treat as distinct from every other.
    /// </summary>
    Lead,

    /// <summary>The lead's principal.</summary>
    Principal,

    /// <summary>A <c>LeadHouseholdMember</c>.</summary>
    HouseholdMember,

    /// <summary>
    /// The principal of an EXISTING customer (BNF-B-23) — reserved, and still unproduced.
    ///
    /// <para>
    /// <b>Why a lead attached to a client keeps using <see cref="Principal"/>.</b> Switching the
    /// subject type on <c>Lead.Kind</c> looked right and is not: a lead whose principal had
    /// already answered as <c>Principal</c> and was attached to a client afterwards would
    /// instantly owe a second, <c>CustomerPrincipal</c> response, its answered row ignored and its
    /// conversion blocked. The subject of a questionnaire asked ON a lead is that lead's
    /// principal, whoever the lead belongs to.
    /// </para>
    ///
    /// <para>
    /// These two values are for questionnaires attached to the CLIENT RECORD, which needs M01 to
    /// own a household first (<c>ClientRelationship</c>). Nothing writes them today.
    /// </para>
    /// </summary>
    CustomerPrincipal,

    /// <summary>A household member of an existing customer (BNF-B-23). Reserved — see above.</summary>
    CustomerHouseholdMember
}

/// <summary>Whether a <see cref="QualificationResponse"/> has been filled in (BNF-B-08).</summary>
public enum QualificationResponseStatus
{
    /// <summary>
    /// Created by the system because the product line expects it, and not yet answered. A
    /// conversion refuses while a required one is still here.
    /// </summary>
    ToComplete,

    Completed
}
