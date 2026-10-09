namespace Sankore.Modules.Integration.Adapters.Orass;

using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// The missing document, named once (ASS-06, last acceptance criterion: « Prérequis :
/// spécification d'interface ORASS et accord de l'assureur »).
///
/// <para>
/// A type rather than a comment, and one place rather than ten literals, because this string is
/// the only thing an operator will ever see from this adapter. It reaches them through the
/// <c>Detail</c> of an <see cref="IntegrationResult"/> — a rejected command, a failed health
/// check, a call-journal row — and it has to tell them the truth: nothing is broken, a supplier
/// deliverable has not arrived, and no change in this repository can lift it.
/// </para>
///
/// <para>
/// It is deliberately not phrased "not implemented". That phrasing sends an operator to raise a
/// defect, and a developer to look for the half-written method that is supposed to exist. Both
/// waste their time: the adapter is as complete as it can be without the insurer.
/// </para>
///
/// <para>
/// <b>TWO COUNTERPARTIES, NOT ONE — and that is the difference from the three core-banking
/// precedents.</b> Perfect Vision, Amplitude and SAB each wait on a vendor: a document, and that
/// is all. ASS-06's prerequisite has two halves and they are owned by different people. The
/// specification comes from ORSYS <i>or from the insurer</i> (« selon la spécification fournie par
/// ORSYS ou l'assureur »), while the <b>agreement</b> comes from the insurer alone — and the
/// agreement is not paperwork around the document, it is a commercial decision that also settles
/// whether the API is open to this intermediary at all, which is the input criterion 2's routing
/// reads. So a refusal has to send the operator to whoever owns the <i>insurer</i> relationship,
/// not merely to whoever chases software vendors; a message naming only ORSYS would have somebody
/// chase a publisher for a permission only their partner can grant.
/// </para>
///
/// <para>
/// <b>And, like Amplitude, one ORASS is two integrations.</b> Where the insurer has opened its API
/// the ports would sit on calls; where it has not, criterion 2 puts them on the batch socle. So
/// "the missing document" is not one artefact but one of two, and the message names <b>the one
/// this installation needs</b> — see <see cref="MissingDocumentFor"/>. An operator who forwards "we
/// need the ORASS specification" gets a conversation; one who forwards "we need the bordereau
/// layout and the acknowledgement format for our apporteur code on the Vie branch" gets a document.
/// </para>
///
/// <para>
/// <b>What this type is NOT allowed to contain</b>, now or ever: an endpoint path, a verb, a header
/// name, a field name, a record type, a file name, an apporteur code. An invented identifier in a
/// message is how an invented identifier reaches a mapper — somebody reads the error, takes the
/// name for documentation, and the guess becomes the contract. A test pins the absence. The words
/// that DO appear — ORASS®Suite, ORSYS, Bancassurance, bordereau, IARD, Vie — are the user story's
/// own and the settings record's own, not ours.
/// </para>
/// </summary>
public static class OrassSpecification
{
    /// <summary>Where the block is recorded, with the three other adapters in the same state.</summary>
    public const string PlanReference = "docs/integration-module-plan.md §6";

    /// <summary>
    /// The publisher of ORASS®Suite, named because one of the two asks is theirs.
    /// </summary>
    public const string Supplier = "ORSYS";

    /// <summary>
    /// Who the operator's next action is actually with. Both, in one phrase, because the
    /// specification may come from either and the agreement can only come from the insurer.
    /// </summary>
    public const string Counterparty = Supplier + " or the insurer";

    /// <summary>
    /// What is missing when we do not know which carrier this installation uses — the settings row
    /// could not be read, carries another kind's settings, or the tenant's ORASS connections do not
    /// agree. Both asks are named, because without the carrier we cannot tell which one is on the
    /// critical path.
    /// </summary>
    public const string MissingDocument =
        "the ORASS interface specification has not been obtained from " + Counterparty
        + ", and the insurer's agreement has not been given (neither the external API or "
        + "Bancassurance operation catalogue nor the bordereau layout and its acknowledgement "
        + "format)";

    /// <summary>
    /// What is missing <b>for this installation</b>, in one sentence an operator can forward to
    /// whoever owns the insurer relationship.
    ///
    /// <para>
    /// Never an operation name, never an endpoint path, never a record layout or a field:
    /// inventing any of them is the failure mode this whole type exists to prevent. The sentence
    /// says what KIND of artefact is wanted, which is exactly as much as we know.
    /// </para>
    /// </summary>
    /// <param name="carrier">
    /// The carrier this installation's writes travel on, or <c>null</c> when it could not be
    /// established. Null does NOT default to one of the two: naming the wrong artefact would send a
    /// conversation after a document that would not unblock this installation.
    /// </param>
    public static string MissingDocumentFor(OrassCarrier? carrier)
        => carrier switch
        {
            OrassCarrier.ExternalApi =>
                "the operation catalogue of the insurer's ORASS external API or Bancassurance "
                + $"module has not been obtained from {Counterparty}",

            OrassCarrier.BatchSocle =>
                "the ORASS bordereau layout and its acknowledgement format have not been obtained "
                + $"from {Counterparty}",

            _ => MissingDocument,
        };

    /// <summary>
    /// The six questions to settle, in the order they unblock work.
    ///
    /// <para>
    /// Ordered, and not a set. Question 1 decides which of 2 and 3/4 is on the critical path — and
    /// only that; it does not decide what gets built, because one SANKORE deployment serves an
    /// insurer with an open API and one fed by file, side by side, and
    /// <c>OrassSettings.BaseUrl</c> is what tells them apart. Questions 3 and 4 are the pair
    /// <c>PerfectVisionSpecification</c> learned to separate: a layout decides whether a file can
    /// be written at all, an acknowledgement format decides whether a written file can ever be
    /// CLOSED (a batch command stays <c>Batched</c> until an acknowledgement closes it — INT-24 and
    /// INT-25, and it waits out <c>AckTimeoutHours</c> before alerting). Question 5 is placed
    /// fifth and not second on purpose: it is a detail OF whichever of 2 to 4 applies and cannot be
    /// asked in the abstract — but it must be settled before the first refusal is removed, because
    /// its answer is what decides whether a mis-attributed submission is ever detectable. Question
    /// 6 is last: an environment obtained before the contracts is an environment nobody can send
    /// anything to.
    /// </para>
    ///
    /// <para>
    /// <b>2 and 3/4 are two separate asks</b>, not two halves of one. They concern different
    /// carriers at different insurers, and an answer to one says nothing about the other — which is
    /// why the health check and every refusal name only the one this installation needs.
    /// </para>
    ///
    /// <para>
    /// Exposed rather than left in a comment so that the registration, the tests and an operator
    /// screen can all print the same list. A procurement conversation that gets three different
    /// versions of "what we need" gets none of them.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> OpenQuestions =
    [
        "1. The insurer's agreement, and which carrier it covers: whether this insurer opens its "
        + "ORASS external API or its Bancassurance module to this intermediary at all, on which "
        + "branch, and under what terms. This is the prerequisite ASS-06 names and it is a "
        + "commercial decision, not a document — it also settles the input criterion 2's routing "
        + "reads, since a connection with no base URL IS the statement that no API was opened. It "
        + "decides which of the two asks below is on the critical path; it removes neither.",

        "2. API carrier — the operation catalogue and its authentication: which operations exist "
        + "for subscribing a policy, cancelling one, declaring a claim, adding a document to a "
        + "claim, listing policies and claims and fetching a certificate; the request and response "
        + "contract of each; how a call is authenticated; and whether a replayed call can be made "
        + "a no-op. That last point is not a detail: every write in this module carries an "
        + "IdempotencyKey precisely because a timeout cannot tell 'not subscribed' from "
        + "'subscribed, answer lost', and an insurer that ignores the key turns our retry into a "
        + "second policy and a second premium on a customer who asked for one. OrassSettings "
        + "already carries BaseUrl and CredentialVaultRef — the catalogue is what no document here "
        + "defines.",

        "3. Batch carrier — the bordereau layout and its encoding: which record types exist, which "
        + "fields each carries, in what order, fixed-width or delimited, and in which code page. "
        + "Separately: whether the insurer accepts ONE bordereau carrying both subscriptions and "
        + "claims or demands one per flow, because that decides whether a connection's cut-off "
        + "produces one file or two. BatchCapableSettings already offers CutOffTime, FileEncoding "
        + "and FieldSeparator, so a delimited file in a non-UTF-8 code page is configurable — but "
        + "not guessable.",

        "4. Batch carrier — the acknowledgement format: what the insurer writes back, under what "
        + "file name, in which directory, and how one line identifies the submission it answers. "
        + "And one question specific to insurance: whether an acknowledgement means 'bordereau "
        + "received' or 'policy issued'. Those are different facts and only the second may ever be "
        + "shown to a customer as cover in force; a socle that closed a command on the first would "
        + "have a counter clerk tell somebody they are insured because a file was accepted. "
        + "Without this format a subscription can be deposited and never closed, which is worse "
        + "than refusing to deposit it.",

        "5. How the intermediary / apporteur code and the branch travel on whichever carrier "
        + "applies — the field, the record column or the parameter that carries each — and whether "
        + "a code is bound to ONE undertaking, so that the insurer itself refuses a submission "
        + "naming the other branch. That is the half that decides whether a mis-attributed "
        + "submission is caught by the insurer or only by us: if a code addresses the whole group, "
        + "the pair we send is the only thing standing between this IMF's portfolio and another "
        + "distributor's, and it can never be verified rather than merely required (see "
        + "OrassIntermediaryScope).",

        "6. A test environment on the insurer's side, covering BOTH branches and stating which "
        + "carrier it exposes. Criterion 4 asks for green contract tests, and a single-branch "
        + "environment cannot green the assertion that matters: that a submission for the IARD "
        + "undertaking never lands in the Vie one, and the reverse. One branch would let the "
        + "branching be wrong and the suite stay green.",
    ];

    /// <summary>
    /// The <c>Detail</c> every specification refusal carries. Names the operation so a call-journal
    /// row is readable on its own, then the branch it was meant for, then the missing artefact for
    /// this installation's carrier, then where the block is recorded.
    /// </summary>
    /// <param name="operation">
    /// The logical operation, from <see cref="OrassOperations"/> — a constant and not a literal, so
    /// that the word in this message is the same word the capability matrix uses.
    /// </param>
    /// <param name="branch">The declared branch, or <c>null</c> when it could not be established.</param>
    /// <param name="carrier">The resolved carrier, or <c>null</c> when it could not be established.</param>
    public static string RefusalDetail(string operation, OrassBranch? branch, OrassCarrier? carrier)
        => $"ORASS cannot serve {operation} on {OrassIntermediaryScope.Designation(branch)}: "
           + $"{MissingDocumentFor(carrier)}. This is a supplier and partner dependency and not a "
           + $"defect — forward it to whoever owns the {Counterparty} relationship, and see "
           + $"{PlanReference}. Writing a plausible mapping without the specification would "
           + "produce an adapter that reports subscriptions the insurer never accepted, which "
           + "means a customer told at a counter that they are covered when they are not.";

    /// <summary>
    /// The same statement for the health check, which answers an operator staring at an activation
    /// screen rather than a queued command.
    /// </summary>
    public static string HealthDetail(OrassBranch? branch, OrassCarrier? carrier)
        => $"ORASS is not operable on {OrassIntermediaryScope.Designation(branch)}: "
           + $"{MissingDocumentFor(carrier)}. See {PlanReference}. The connection may be "
           + "configured and will stay inactive until the specification arrives and the insurer "
           + "agrees.";
}

/// <summary>
/// The logical operation names, as constants.
///
/// <para>
/// Same reason as <c>TemenosOperations</c>, <c>PerfectVisionOperations</c> and
/// <c>SabOperations</c>: the word appears in a refusal detail and in the capability matrix, and a
/// typo would give an administrator reading the matrix and an operator reading a rejection two
/// different vocabularies for one operation.
/// </para>
///
/// <para>
/// All but one match the <see cref="IntegrationCapability"/> names exactly.
/// <see cref="AddClaimDocument"/> is the exception and it is named here rather than left implicit:
/// <c>IInsuranceClaimPort.AddDocumentAsync</c> has no capability of its own and no
/// <c>CommandType</c>, so it rides on <see cref="IntegrationCapability.DeclareClaim"/> — the
/// follow-up an insurer asks for after a declaration belongs to the declaration. Worth knowing
/// before somebody adds a capability for it: doing so would also need a command type and a
/// dispatcher route, since the method takes an <c>IdempotencyKey</c> and is therefore a write.
/// </para>
/// </summary>
public static class OrassOperations
{
    public const string CheckHealth = "CheckHealth";
    public const string SubscribePolicy = "SubscribePolicy";
    public const string ReadPolicies = "ReadPolicies";
    public const string IssueCertificate = "IssueCertificate";
    public const string CancelPolicy = "CancelPolicy";
    public const string DeclareClaim = "DeclareClaim";
    public const string ReadClaims = "ReadClaims";

    /// <summary>The one name with no <see cref="IntegrationCapability"/> behind it.</summary>
    public const string AddClaimDocument = "AddClaimDocument";
}
