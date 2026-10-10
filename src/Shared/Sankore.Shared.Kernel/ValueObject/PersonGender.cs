namespace Sankore.Shared.Kernel.ValueObject;

/// <summary>
/// Gender of a natural person, as carried by <see cref="PersonIdentity"/>.
///
/// <para>
/// A fourth gender enum in this solution, and deliberately so. <c>Customers.Domain.Gender</c>
/// and <c>Leads.Domain.LeadGender</c> already exist and belong to their aggregates; this one
/// belongs to the shared value object, which several modules map into their own schema. It is
/// named <c>PersonGender</c> rather than <c>Gender</c> because M01 uses <c>Gender</c> for its own
/// enum — two types of the same simple name in scope make every file that touches both ambiguous.
/// </para>
///
/// <para>
/// <see cref="Unknown"/> is first, so it is <c>default</c>: an owned type whose columns were never
/// written reads as "we do not know", not as a person's gender picked by field order.
/// </para>
/// </summary>
public enum PersonGender
{
    Unknown,
    Male,
    Female,
    Other
}
