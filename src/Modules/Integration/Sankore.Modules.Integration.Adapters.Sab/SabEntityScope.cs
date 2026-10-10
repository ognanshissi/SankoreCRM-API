namespace Sankore.Modules.Integration.Adapters.Sab;

using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// Whether a call can be scoped to one institution (INT-32, criterion 2: « il gère l'entité
/// (<c>Entity</c>) pour les réseaux multi-IMF comme le réseau CIF »).
///
/// <para>
/// <b>This decision is ours, not SBS's</b> — which is why it is real code while the call it will
/// one day guard is not. The condition is one configured value: an installation that belongs to a
/// multi-IMF network names its entity, and one that does not still has to, because nothing in a
/// settings row says which kind of installation it is. Nothing about that branch depends on the
/// catalogue, so it is implemented and tested in full.
/// </para>
/// </summary>
public enum SabEntityResolution
{
    /// <summary>
    /// An entity is configured, so a call could say which institution it is about. What this is
    /// NOT is a statement that the entity EXISTS, or that our API key may address it — see the
    /// remarks on <see cref="SabEntityScope"/>, which explain why that cannot be established
    /// without the catalogue and what has to be built the day it arrives.
    /// </summary>
    Resolved,

    /// <summary>
    /// No entity. Every call must refuse, and refuse as a configuration fault the tenant's own
    /// administrator can clear in a minute — never as the pending catalogue, which is a
    /// procurement conversation.
    /// </summary>
    Missing
}

/// <summary>
/// The entity rule of criterion 2, as a pure function of the connection's settings.
///
/// ───────────────────────────────────────────────────────────────────────────────────────────────
/// <b>WHY THIS IS A SECURITY PROPERTY AND NOT A FORMALITY.</b> On a multi-IMF network such as CIF
/// (Confédération des Institutions Financières), one Open SAB installation serves several
/// institutions, and <c>Entity</c> is what says which one a call is about. A call that omits it
/// lands on whatever entity Open SAB defaults to; a call carrying the wrong one reads or writes
/// another institution's customers. In a hosted multi-tenant deployment that is a cross-tenant
/// data leak in which no HTTP boundary of ours is ever crossed — no 403 is answered anywhere,
/// nothing appears in a log as a denied request, and the only trace is a customer record created
/// for the wrong bank.
///
/// <b>WHAT THE ADAPTER GUARANTEES AT CALL TIME.</b> Not one port method, and not the health
/// check, may proceed past a connection whose <c>Entity</c> is absent. The check is the FIRST
/// thing every method does — before the credential, before the catalogue refusal — and it fails
/// closed. <c>ConnectionSettingsValidator</c> already makes <c>settings.entity</c> required when a
/// connection is created or updated (INT-03); this is the other half, and the two are not
/// redundant: the validator protects the rows written through the API from today onwards, while
/// this protects the call from a row that got there another way — written before that rule
/// existed, restored from a backup taken before it, produced by a future migration that adds a
/// field and rewrites the jsonb, or edited straight in the database during an incident.
///
/// <b>WHICH DIRECTION IT FAILS, AND WHY THAT DIRECTION.</b> It refuses. The two mistakes are not
/// comparable: refusing a call that might have worked costs an administrator one field in a form
/// and a retry, while making a call that might be unscoped costs another institution its
/// customers' confidentiality and cannot be undone by noticing afterwards. Fail-closed is also
/// the cheaper default to live with here — this adapter refuses everything today anyway, so the
/// guard's only observable effect is on the day somebody starts removing refusals, which is
/// exactly the day it must already be on the call path rather than waiting to be remembered.
///
/// <b>WHAT CANNOT BE GUARANTEED, SAID OUT LOUD.</b> Presence is all that is verifiable here.
/// Whether the configured entity exists, and whether this connection's API key is entitled to
/// address it, are questions only Open SAB can answer — and both ways of asking need the
/// catalogue: an entity-listing service to check the value against, or the knowledge that a key is
/// bound to one entity so that the far end refuses the rest (question 2 of
/// <see cref="SabSpecification.OpenQuestions"/>). So the dangerous case this guard does NOT catch
/// is a well-formed entity code belonging to another institution of the same network, typed in by
/// mistake: it is indistinguishable from the right one until something answers. The day the
/// catalogue arrives, the obligation is therefore on <c>CheckHealthAsync</c> and not on a port —
/// it must prove the configured entity is one the key may address, and leave the connection
/// unactivatable when it is not, because activation is the single gate the platform already makes
/// every command pass through.
///
/// <b>WHAT IS DELIBERATELY NOT CHECKED.</b> Length, character set, case, a prefix convention —
/// nothing. Every one of those is a property of the catalogue, and a shape rule invented here
/// would refuse a legitimate entity for a reason no document supports, which is the same class of
/// mistake as a guessed field name pointing the other way.
/// ───────────────────────────────────────────────────────────────────────────────────────────────
///
/// <para>
/// Static and settings-only on purpose: it is the one piece of INT-32 that can be verified today,
/// and a test of it must not need a database, a tenant or an adapter instance. The adapter calls
/// it; so does <see cref="SabCapabilityMatrix"/>, so the matrix a screen reads and the branch a
/// call takes can never disagree — an installation that cannot be addressed must not be shown
/// buttons.
/// </para>
/// </summary>
public static class SabEntityScope
{
    /// <summary>
    /// <see cref="SabEntityResolution.Missing"/> whenever no usable entity is configured — null,
    /// absent settings, or whitespace.
    ///
    /// <para>
    /// Whitespace counts as absent deliberately. An entity arrives through INT-03's settings form,
    /// and a cleared field that posts <c>" "</c> means the administrator removed it; treating it as
    /// a value would send a call scoped to a blank institution, which is precisely the "whatever
    /// Open SAB defaults to" case.
    /// </para>
    ///
    /// <para>
    /// Null settings also mean <c>Missing</c> rather than an exception. The caller may be the
    /// capability matrix of a tenant that has configured no SAB connection at all, and "there is
    /// no entity" is the correct answer to that, not a fault.
    /// </para>
    /// </summary>
    public static SabEntityResolution ResolveFor(SabSettings? settings)
        => IsScoped(settings) ? SabEntityResolution.Resolved : SabEntityResolution.Missing;

    /// <summary>The same condition, named, for the matrix and for assertions.</summary>
    public static bool IsScoped(SabSettings? settings)
        => !string.IsNullOrWhiteSpace(settings?.Entity);

    /// <summary>
    /// The entity a call would carry, or <c>null</c> when there is none.
    ///
    /// <para>
    /// Trimmed, because a settings form posts what was pasted and a trailing space is not part of
    /// an institution's code. Trimming is the ONLY normalisation applied: case-folding or padding
    /// would be a guess about how Open SAB compares the value, and a guess that turned one
    /// institution's code into another's is the whole risk of this file.
    /// </para>
    ///
    /// <para>
    /// Nothing calls this today — no call is built — and it exists anyway so that the value a
    /// future transport sends is read through the same function that decided the call was allowed.
    /// A transport reading <c>settings.Entity</c> directly is how a blank gets past a guard that
    /// said there was an entity.
    /// </para>
    /// </summary>
    public static string? EntityOf(SabSettings? settings)
        => IsScoped(settings) ? settings!.Entity!.Trim() : null;

    /// <summary>
    /// The <c>Detail</c> an unscoped connection is refused with.
    ///
    /// <para>
    /// It names the field, the network, and the consequence, and it says explicitly that nothing
    /// is being waited on from SBS — otherwise an operator who has just read ten refusals about a
    /// missing catalogue reads an eleventh and forwards it to procurement, when the fix is one
    /// field on a screen they already have open. Reported with
    /// <see cref="IntegrationErrors.SettingsInvalid"/> for the same reason.
    /// </para>
    /// </summary>
    public static string MissingDetail()
        => "This SAB connection configures no Open SAB entity (SabSettings.Entity is empty), so no "
           + "call can be scoped to an institution. On a multi-IMF network such as CIF one Open SAB "
           + "installation serves several institutions: a call that does not name its entity lands "
           + "on whichever entity Open SAB defaults to, which may be another institution's. "
           + "Set settings.entity on the connection (INT-03) — nothing is being waited on from SBS "
           + "for this.";
}
