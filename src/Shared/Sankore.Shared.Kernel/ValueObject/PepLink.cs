namespace Sankore.Shared.Kernel.ValueObject;

/// <summary>
/// How a person is connected to a politically exposed function (BNF-B-02, BNF-B-05).
///
/// <para>
/// The link matters as much as the exposure itself: the LBC/FT obligations of the UEMOA apply to
/// the family and close associates of a PEP, not only to the office holder, so a status that only
/// said "exposed" could not express the majority of real cases.
/// </para>
/// </summary>
public enum PepLink
{
    /// <summary>The person holds, or held, the exposed function.</summary>
    Self,

    /// <summary>Spouse, child, parent or sibling of a person who does.</summary>
    FamilyMember,

    /// <summary>Known close business or personal associate of a person who does.</summary>
    CloseAssociate
}
