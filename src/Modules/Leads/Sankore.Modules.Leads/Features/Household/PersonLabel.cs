namespace Sankore.Modules.Leads.Features.Household;

using Sankore.Modules.Leads.Domain;

/// <summary>
/// Who a per-person item is about, in words — "Le titulaire", "Le conjoint (Awa)", "L'enfant Kofi".
///
/// <para>
/// One definition because three screens now show it: the questionnaires owed, the questionnaires
/// answered, and the completeness report. They had a copy each, and three copies of a label is
/// three chances for the same child to be called something different on three screens of the same
/// file.
/// </para>
///
/// <para>
/// The NAME and nothing else. A label shown on a completeness screen must not carry a date of
/// birth or an address just because the object it came from had them.
/// </para>
/// </summary>
internal static class PersonLabel
{
    internal const string Principal = "Le titulaire";

    /// <summary>A member of the lead's household, or the fallback when the id resolves to nobody.</summary>
    internal static string For(Lead lead, QualificationSubjectType type, Guid subjectId)
    {
        if (type is QualificationSubjectType.Principal
                 or QualificationSubjectType.Lead
                 or QualificationSubjectType.CustomerPrincipal)
            return Principal;

        var member = lead.HouseholdMembers.FirstOrDefault(m => m.Id == subjectId);
        return member is null ? "Un membre du foyer" : For(member);
    }

    internal static string For(LeadHouseholdMember member)
        => member.Type == HouseholdMemberType.Spouse
            ? $"Le conjoint ({member.Identity.FirstName})"
            : $"L'enfant {member.Identity.FirstName}";
}
