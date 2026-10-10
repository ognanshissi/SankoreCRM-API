namespace Sankore.Modules.Leads.Features.CaptureLead;

/// <summary>
/// The ONE rule for turning whatever names an inbound lead carries into the name a lead must have
/// (BNF-B-18).
///
/// <para>
/// <b>Why it is shared.</b> Four inbound paths need it — the capture endpoint, the web ingest, the
/// scheduled pull and its dry run — and three of them had their own answer. The pull job and the
/// dry run derived <c>"{first} {last}"</c>; the web ingest REQUIRED <c>fullName</c> and rejected
/// the payload with <c>REQUIRED_FIELDS_MISSING:fullName</c> when it had the structured pair
/// instead. So a v2 web form collecting a first and last name was refused by the very route the
/// SDK posts to, while the same payload pulled from a partner's API was accepted. One rule, in
/// one place, is the fix.
/// </para>
///
/// <para>
/// <b>A supplied full name WINS.</b> A caller that sent one meant it — a prospect whose legal name
/// and usual name differ is ordinary in the UEMOA zone, and recomposing "Prénom Nom" over the top
/// would overwrite what the agent or the form captured. The aggregate still derives
/// <c>FullName</c> from the structured pair on a later UPDATE (BNF-B-01), which is a different
/// moment and a deliberate one.
/// </para>
/// </summary>
internal static class LeadNameResolver
{
    /// <summary>
    /// The name to store, or <c>null</c> when the payload carries none — which every caller turns
    /// into a refusal rather than a placeholder: a lead called "[sans nom]" on a dispatch queue is
    /// worse than a rejected payload somebody can resend.
    /// </summary>
    internal static string? Resolve(string? fullName, string? firstName, string? lastName)
    {
        if (!string.IsNullOrWhiteSpace(fullName))
            return fullName.Trim();

        var first = firstName?.Trim();
        var last = lastName?.Trim();

        if (!string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(last))
            return $"{first} {last}";

        // One of the two alone is enough to call somebody by, and refusing over a missing surname
        // would lose prospects a web form never asks it of.
        if (!string.IsNullOrWhiteSpace(first)) return first;
        if (!string.IsNullOrWhiteSpace(last)) return last;

        return null;
    }
}
