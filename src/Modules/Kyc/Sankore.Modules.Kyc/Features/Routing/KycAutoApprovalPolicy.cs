namespace Sankore.Modules.Kyc.Features.Routing;

using System.Globalization;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure.Storage;

/// <summary>
/// KRM-B-08 — decides whether a remotely collected file may be approved with NO human, and says
/// why not when it may not. Section 3.2 of the specification, and nothing else.
///
/// <para>
/// <b>A pure function over a record, deliberately.</b> No <c>DbContext</c>, no module contract, no
/// clock: the handler gathers the facts and this class weighs them. That is what makes
/// "règles d'approbation automatique couvertes par des tests unitaires cas par cas" — the
/// specification's own definition-of-done — a sentence a unit test can satisfy, instead of a
/// scenario needing a scored file, a product, a tenant and a mail server.
/// </para>
///
/// <para>
/// <b>It only ever approves.</b> There is no verdict that refuses a customer. Every failed
/// condition routes to a human who decides between a complement and a refusal — including a score
/// the service rejected outright, which is the one case where a machine refusal would look most
/// defensible and is still not ours to take. That asymmetry is the whole compliance argument for
/// the feature: the automatic path can only ever skip a signature the institution would have
/// given anyway.
/// </para>
///
/// <para>
/// <b>Every condition is evaluated, not short-circuited</b>, so the stored verdict can show a
/// compliance officer which rules passed on a file that was routed out on one. Only the REASON is
/// first-failure, in the order the conditions are declared below — configuration before evidence,
/// so an officer who switched the feature off is told that, rather than being sent to look at a
/// score.
/// </para>
/// </summary>
internal static class KycAutoApprovalPolicy
{
    /// <summary>
    /// Everything the decision rests on, gathered by the handler. One record rather than fifteen
    /// parameters because a test case IS an instance of it, and because adding a condition then
    /// shows up as a compile error at the one call site that has to supply the new fact.
    /// </summary>
    /// <param name="TenantAllowsAutoApproval">
    /// <c>ComplianceSettings.KycAutoApprovalEnabled</c>. False for an outage reading it, which is
    /// the fail-closed direction: a machine must not approve because M12 was unreachable.
    /// </param>
    /// <param name="TenantMaxTier">
    /// <c>ComplianceSettings.KycAutoApprovalMaxTier</c>, as the string M12 stores — M12 cannot
    /// reference this module's <see cref="KycTier"/>. An unreadable value fails closed.
    /// </param>
    /// <param name="PolicyEnabled">The product line still allows remote collection.</param>
    /// <param name="PolicyIsAutomatic">
    /// The product's <c>ValidationMode</c> is <c>Automatic</c>. False — the default — is the
    /// specification's "Mode Manual du produit : routage manuel direct, motif PRODUCT_POLICY".
    /// </param>
    /// <param name="RequiredTier">The tier this product grants, as the product stores it.</param>
    /// <param name="MinAutoScore">The product's score floor for an unattended approval.</param>
    /// <param name="ConfidenceLevel">
    /// Null when no verification has produced one — a file routed before it was ever scored, which
    /// fails the threshold rather than passing it.
    /// </param>
    /// <param name="ConfidenceScore">The global score, 0-100, or null for the same reason.</param>
    /// <param name="Flags">
    /// The scoring service's flags, VERBATIM. Matched case-insensitively against
    /// <see cref="KycBlockingFlags"/> and never parsed.
    /// </param>
    /// <param name="IdentityMatchesRecord">
    /// Whether the name, given names and date of birth READ on the document agree with the client
    /// record — M01's answer, because M01 holds the record. False when either side is missing: a
    /// document whose name could not be read is not a document that agrees with anything.
    /// </param>
    /// <param name="FaceMatchedFirstTry">
    /// The faces matched, on attempt one. A second attempt means somebody's first selfie was
    /// rejected, which <c>KycApprovalCircuit</c> already treats as a signal about the capture.
    /// </param>
    /// <param name="HasOpenEventReview">
    /// An event-driven <c>KycReviewSchedule</c> is open on this file. This is how the
    /// specification's "aucune alerte LBC-FT ouverte" is read in this platform — see the remarks
    /// on <see cref="Evaluate"/>.
    /// </param>
    /// <param name="MissingDocumentKinds">
    /// Required kinds not on file. Computed by <c>KycRemoteJourney</c>, so the policy and the
    /// customer's own send button agree about what "complete" means.
    /// </param>
    /// <param name="DocumentsOverRetryBudget">
    /// Kinds the customer photographed more times than the tenant's budget allows. KRM-B-06 lets
    /// the upload through past that point; this is the other half of the bargain.
    /// </param>
    /// <param name="RemoteSaleEvidenceSatisfied">
    /// <c>RemoteSaleEvidence.EnsureSatisfiedAsync</c>'s verdict — CIMA LBC/FT article 13.6's proof
    /// of address. Passed in rather than re-derived, so the automatic path and the final rung of
    /// the manual circuit consult the SAME gate and cannot drift apart.
    /// </param>
    internal sealed record Input(
        bool TenantAllowsAutoApproval,
        string? TenantMaxTier,
        bool PolicyEnabled,
        bool PolicyIsAutomatic,
        string? RequiredTier,
        int MinAutoScore,
        KycConfidenceLevel? ConfidenceLevel,
        int? ConfidenceScore,
        IReadOnlyCollection<string> Flags,
        bool IdentityMatchesRecord,
        bool IsPoliticallyExposed,
        KycVigilanceLevel VigilanceLevel,
        bool DuplicateSuspected,
        bool FaceMatchedFirstTry,
        bool HasOpenEventReview,
        IReadOnlyCollection<KycDocumentKind> MissingDocumentKinds,
        IReadOnlyCollection<string> DocumentsOverRetryBudget,
        bool RemoteSaleEvidenceSatisfied);

    /// <summary>One condition's verdict, as it is stored on the decision.</summary>
    /// <param name="Rule">
    /// The reason code this condition OWNS — so a passing rule and the reason it would have
    /// produced are the same identifier, and a report can line them up.
    /// </param>
    /// <param name="Detail">
    /// Why it failed, for a human reading the stored verdict. Never a value read on the document,
    /// never an identity — a flag name, a score against its floor, a count.
    /// </param>
    internal sealed record RuleOutcome(string Rule, bool Passed, string? Detail = null);

    /// <param name="Reason">
    /// Null when <paramref name="Automatic"/> is true; otherwise the first failing condition's
    /// code, which is what the file stores and the agent's screen translates.
    /// </param>
    /// <param name="Tier">
    /// The tier to grant — only meaningful when <paramref name="Automatic"/> is true. It is the
    /// PRODUCT's required tier and never a reading of the score: a tier is what the institution
    /// promised for this line, and the score is the evidence that the promise can be kept.
    /// </param>
    /// <param name="Rules">Every condition's verdict, in declaration order.</param>
    internal sealed record Decision(
        bool Automatic,
        string? Reason,
        KycTier Tier,
        IReadOnlyList<RuleOutcome> Rules);

    /// <summary>
    /// Weighs section 3.2.
    ///
    /// <para>
    /// <b>Three places where the specification cannot be followed to the letter, and what is done
    /// instead.</b>
    /// </para>
    ///
    /// <list type="number">
    /// <item>
    ///   <b>« Niveau de confiance Validated ».</b> There is no such level.
    ///   <see cref="KycConfidenceLevel"/> is Rejected / Low / Medium / High, and the biometric
    ///   service's own grades are Low / Medium / High — "Validated" is not a grade anything
    ///   produces, it is the file's STATUS after somebody approves it, so reading the condition
    ///   literally would make it unsatisfiable. It is read as <c>High</c>, which is the strictest
    ///   available grade, <b>and</b> the score floor is still applied on top. Mapping it to
    ///   "anything but Rejected" would have let a Low-confidence file through on a score that
    ///   cleared a product's floor, which is not what a condition naming the top grade can mean.
    /// </item>
    /// <item>
    ///   <b>« aucune alerte LBC-FT ouverte ».</b> This platform has no AML-alert entity — M01
    ///   carries a client's risk level, activity sector and funds origin, and M02 carries reviews.
    ///   The nearest true reading is "this file is under heightened watch right now", which is an
    ///   OPEN event-driven <c>KycReviewSchedule</c> (what <c>ClientPepExposureRaisedConsumer</c>
    ///   and <c>RaiseKycReviewCommand</c> produce) together with the file's own
    ///   <c>DuplicateSuspected</c> flag. Both fold into the conditions below rather than becoming
    ///   a new code, so the specification's reason list stands. Should a transaction-monitoring
    ///   alert ever exist, it joins <see cref="Input.HasOpenEventReview"/> and nothing else moves.
    /// </item>
    /// <item>
    ///   <b>CIMA article 13.6's proof of address is enforced here too, and the specification does
    ///   not mention it.</b> <c>RemoteSaleEvidence</c> refuses the final approval of any remote
    ///   file without an ACCEPTED proof of address carrying an issue date within the tenant's
    ///   freshness window, and <c>RemoteSelfService</c> is a remote channel. Omitting it would let
    ///   the automatic path grant a tier that the manual path, on the very same file, would have
    ///   refused — so it is a condition like the others, under
    ///   <see cref="KycRoutingReasons.ProofOfAddressRequired"/>.
    ///   <para>
    ///   <b>The operational consequence is real and worth stating.</b> "Accepted" is a reviewer's
    ///   decision and the issue date is a reviewer's entry, so until somebody at the institution
    ///   reviews a customer's proof of address, no remotely collected file of a product that is
    ///   subject to article 13.6 can be approved automatically — it routes to a human under this
    ///   code. Whether that is the intended reach of the automatic mode, or whether the remote
    ///   journey should collect the issue date from the customer and the policy should accept an
    ///   unreviewed proof, is a compliance decision for each institution and is NOT taken here.
    ///   Taking it in code would be deciding, in a routing class, how much of an LBC/FT obligation
    ///   a machine may discharge.
    ///   </para>
    /// </item>
    /// <item>
    ///   <b>The blocking flags are not a type.</b> They arrive as strings from the scoring service
    ///   and are stored verbatim, so <see cref="KycBlockingFlags"/> is a documented constant list
    ///   matched case-insensitively. A flag it does not know is not blocking — widening that list
    ///   is a compliance decision somebody takes, not something a service deployment does to an
    ///   institution overnight.
    /// </item>
    /// </list>
    /// </summary>
    internal static Decision Evaluate(Input input)
    {
        var tier = ParseTier(input.RequiredTier);
        var rules = new List<RuleOutcome>(11);

        // ── Configuration, first ────────────────────────────────────────────
        // Before any evidence, because an officer who turned the feature off, or a product saved
        // in manual mode, must be told THAT — not sent to examine a score that was never going to
        // be consulted.

        rules.Add(new RuleOutcome(
            KycRoutingReasons.ProductPolicy,
            input.PolicyEnabled && input.PolicyIsAutomatic,
            input.PolicyEnabled
                ? "Le produit demande une validation manuelle."
                : "Le produit n'autorise plus la collecte à distance."));

        rules.Add(new RuleOutcome(
            KycRoutingReasons.AutoApprovalDisabled,
            input.TenantAllowsAutoApproval,
            "L'établissement n'autorise pas l'approbation sans intervention humaine."));

        // The ceiling, and it fails closed twice over: an unparsable product tier and an
        // unparsable tenant ceiling both land on "no". KRM-B-01 refuses to SAVE a product above
        // the ceiling, so reaching here means the ceiling was lowered afterwards — which is a
        // compliance officer's prerogative and has to bite immediately on files in flight.
        rules.Add(new RuleOutcome(
            KycRoutingReasons.TierAboveAutoLimit,
            tier != KycTier.None && WithinCeiling(tier, input.TenantMaxTier),
            $"Le produit exige le niveau {input.RequiredTier ?? "(inconnu)"}, "
            + $"au-delà du plafond {input.TenantMaxTier ?? "(inconnu)"} de l'établissement."));

        // ── Evidence ────────────────────────────────────────────────────────

        var scoreOk = input.ConfidenceLevel == KycConfidenceLevel.High
                   && input.ConfidenceScore is { } score
                   && score >= input.MinAutoScore;

        rules.Add(new RuleOutcome(
            KycRoutingReasons.ScoreBelowThreshold,
            scoreOk,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Niveau {input.ConfidenceLevel?.ToString() ?? "(aucun)"}, "
                + $"score {input.ConfidenceScore} pour un minimum de {input.MinAutoScore}.")));

        // DuplicateSuspected is folded in here rather than given its own condition: the
        // specification's own blocking list already names DUPLICATE_DOCUMENT_SUSPECTED, and the
        // flag is this module's durable record of the same fact — KycDuplicateDetection sets it
        // from the document-number collision, which is not something the scorer sees.
        var blocking = KycBlockingFlags.FirstBlocking(input.Flags)
            ?? (input.DuplicateSuspected ? KycBlockingFlags.DuplicateDocumentSuspected : null);

        rules.Add(new RuleOutcome(
            KycRoutingReasons.BlockingFlag,
            blocking is null,
            blocking is null ? null : $"Signalement bloquant : {blocking}."));

        rules.Add(new RuleOutcome(
            KycRoutingReasons.IdentityMismatch,
            input.IdentityMatchesRecord,
            "Le nom, les prénoms ou la date de naissance lus sur la pièce ne correspondent pas "
            + "à la fiche client."));

        rules.Add(new RuleOutcome(
            KycRoutingReasons.Pep,
            !input.IsPoliticallyExposed,
            "Personne politiquement exposée : l'autorisation relève de la direction générale."));

        // Vigilance AND the open review, under one code. See the remarks: an open event-driven
        // review is this platform's "alerte LBC-FT ouverte", and both say the same thing to the
        // agent — this file is being watched, somebody looks at it.
        rules.Add(new RuleOutcome(
            KycRoutingReasons.HighVigilance,
            input.VigilanceLevel != KycVigilanceLevel.High && !input.HasOpenEventReview,
            input.HasOpenEventReview
                ? "Une revue de vigilance est ouverte sur ce dossier."
                : "Vigilance renforcée."));

        rules.Add(new RuleOutcome(
            KycRoutingReasons.FaceRetry,
            input.FaceMatchedFirstTry,
            "La comparaison faciale n'a pas abouti dès la première tentative."));

        rules.Add(new RuleOutcome(
            KycRoutingReasons.MissingDocument,
            input.MissingDocumentKinds.Count == 0,
            $"Pièces manquantes : {string.Join(", ", input.MissingDocumentKinds)}."));

        rules.Add(new RuleOutcome(
            KycRoutingReasons.QualityRetriesExceeded,
            input.DocumentsOverRetryBudget.Count == 0,
            $"Reprises de photo excessives : {string.Join(", ", input.DocumentsOverRetryBudget)}."));

        // LAST, and last on purpose. It is the condition most likely to be the one failing on a
        // correctly configured tenant, and a reason code is first-failure — so putting it ahead of
        // the evidence rules would hide a bad score behind a missing utility bill, and an officer
        // tuning the feature would never see the conditions that actually discriminate.
        rules.Add(new RuleOutcome(
            KycRoutingReasons.ProofOfAddressRequired,
            input.RemoteSaleEvidenceSatisfied,
            "Aucun justificatif de domicile accepté et daté de moins de la durée paramétrée "
            + "(CIMA LBC/FT article 13.6)."));

        var firstFailure = rules.FirstOrDefault(r => !r.Passed);

        return firstFailure is null
            ? new Decision(Automatic: true, Reason: null, Tier: tier, Rules: rules)
            : new Decision(Automatic: false, Reason: firstFailure.Rule, Tier: tier, Rules: rules);
    }

    /// <summary>
    /// The product's tier, or <see cref="KycTier.None"/> for anything unreadable — which the
    /// ceiling condition then refuses. <see cref="KycTier.None"/> is not a tier that can be
    /// granted, so an unconfigured product cannot be approved by accident.
    /// </summary>
    private static KycTier ParseTier(string? raw)
        => Enum.TryParse<KycTier>(raw, ignoreCase: true, out var parsed) && parsed != KycTier.None
            ? parsed
            : KycTier.None;

    /// <summary>
    /// Whether <paramref name="required"/> sits at or below the tenant's ceiling.
    ///
    /// <para>
    /// Compared on the enum's ORDER (None &lt; Simplified &lt; Full) and not by equality, so a
    /// ceiling of <c>Full</c> admits a Simplified product. An unparsable ceiling returns false:
    /// the one value a corrupted compliance setting must not mean is "no limit".
    /// </para>
    /// </summary>
    private static bool WithinCeiling(KycTier required, string? tenantMaxTier)
        => Enum.TryParse<KycTier>(tenantMaxTier, ignoreCase: true, out var ceiling)
        && ceiling != KycTier.None
        && required <= ceiling;
}
