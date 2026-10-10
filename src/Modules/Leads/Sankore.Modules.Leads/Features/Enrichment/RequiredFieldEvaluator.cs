namespace Sankore.Modules.Leads.Features.Enrichment;

using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel.ValueObject;

/// <summary>
/// One person a completeness check is about — the principal, or one active household member.
/// </summary>
/// <param name="Identity">
/// The identity to EVALUATE, with an inherited address already applied. Null for a principal whose
/// structured names are still missing, which is itself reported.
/// </param>
/// <param name="MemberId">Null for the principal.</param>
internal sealed record CompletenessSubject(
    string Label,
    PersonIdentity? Identity,
    Guid? MemberId,
    bool IsPrincipal,
    bool PepDeclared,
    bool InheritsPrincipalAddress);

/// <summary>
/// Evaluates one <see cref="PersonFieldCode"/> against one person (BNF-B-04).
///
/// <para>
/// Split out of the calculator because this is the part worth reading on its own: it is the
/// complete mapping from a configured code to a value, and the only place that decides what
/// "present" means for each field. A missing case here is a requirement that silently passes, so
/// the switch is exhaustive and its default REFUSES rather than succeeds.
/// </para>
/// </summary>
internal static class RequiredFieldEvaluator
{
    /// <summary>
    /// Whether <paramref name="code"/> is satisfied for <paramref name="subject"/>.
    /// </summary>
    /// <param name="lead">
    /// Needed for the principal-only codes, which live on the lead rather than on the identity —
    /// the telephone number, the e-mail, the national id, the amount.
    /// </param>
    internal static bool IsSatisfied(string code, CompletenessSubject subject, Lead lead)
    {
        var identity = subject.Identity;

        return code switch
        {
            // The two names are what PersonIdentity requires, so for a household member they are
            // present by construction. For the principal they are exactly what may be missing.
            PersonFieldCode.LastName   => identity is not null && !string.IsNullOrWhiteSpace(identity.LastName),
            PersonFieldCode.FirstName  => identity is not null && !string.IsNullOrWhiteSpace(identity.FirstName),
            PersonFieldCode.UsageName  => !string.IsNullOrWhiteSpace(identity?.UsageName),
            PersonFieldCode.BirthDate  => identity?.BirthDate is not null,
            PersonFieldCode.Gender     => identity is { Gender: not PersonGender.Unknown },
            PersonFieldCode.Address    => !string.IsNullOrWhiteSpace(identity?.AddressLine),
            PersonFieldCode.PostalCode => !string.IsNullOrWhiteSpace(identity?.PostalCode),
            PersonFieldCode.City       => !string.IsNullOrWhiteSpace(identity?.City),
            PersonFieldCode.Country    => !string.IsNullOrWhiteSpace(identity?.CountryCode),

            // Declared, not negative. PepStatus.NotExposed is the DEFAULT of an owned type and so
            // cannot stand for an answer — see Lead.PepDeclaredAt.
            PersonFieldCode.PepStatus  => subject.PepDeclared,

            PersonFieldCode.Phone      => !string.IsNullOrWhiteSpace(lead.PhoneNumber),
            PersonFieldCode.Email      => !string.IsNullOrWhiteSpace(lead.Email)
                                       || !string.IsNullOrWhiteSpace(lead.LegacyEmailClear),
            PersonFieldCode.NationalId => !string.IsNullOrWhiteSpace(lead.NationalId),
            PersonFieldCode.DesiredAmount => lead.DesiredAmount is { Amount: > 0 },

            // A code M12 accepted and this switch does not handle. It cannot normally happen —
            // BeneficiaryPolicy.Create refuses an unknown code — but a policy stored before that
            // validation existed can still carry one, and a new PersonFieldCode constant added
            // without a case here would land exactly here. UNSATISFIED, never satisfied: a
            // requirement nothing evaluates must block rather than silently pass.
            _ => false,
        };
    }

    /// <summary>
    /// Where the agent has to go to fix a gap, or null when the lead itself is the place.
    ///
    /// <para>
    /// A member that inherits the principal's address has no postal fields of its own — the
    /// inheritance is a link, not a copy — so an address gap on such a member is fixed on the
    /// PRINCIPAL. Without this the completeness screen would ask an agent to fill a field that
    /// the member's own update command refuses (<c>HOUSEHOLD_MEMBER_NEEDS_OWN_ADDRESS</c>).
    /// </para>
    /// </summary>
    internal static string? ActionHintFor(string code, CompletenessSubject subject)
        => subject is { InheritsPrincipalAddress: true, IsPrincipal: false }
           && code is PersonFieldCode.Address or PersonFieldCode.PostalCode or PersonFieldCode.City
               ? "FIX_ON_PRINCIPAL"
               : null;
}
