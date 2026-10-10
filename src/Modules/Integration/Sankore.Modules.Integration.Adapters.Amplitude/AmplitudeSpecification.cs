namespace Sankore.Modules.Integration.Adapters.Amplitude;

using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// The missing document, named once (INT-31, last acceptance criterion: « Prérequis : contrat
/// d'interface et accès API obtenus auprès de SBS »).
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
/// waste their time: the adapter is as complete as it can be without SBS.
/// </para>
///
/// <para>
/// <b>What differs from <c>PerfectVisionSpecification</c>, and it is the whole of INT-31:</b> one
/// Amplitude is two integrations. On Amplitude Up the ports would sit on API services; on an
/// earlier release they would sit on the batch socle. So "the missing document" is not one
/// artefact but one of two, and the message names <b>the one this installation needs</b> — see
/// <see cref="MissingDocumentFor"/>. An operator who forwards "we need the Amplitude
/// specification" to SBS gets a conversation; one who forwards "we need the Up API service
/// catalogue for this release" gets a document.
/// </para>
/// </summary>
public static class AmplitudeSpecification
{
    /// <summary>Where the block is recorded, with the three other adapters in the same state.</summary>
    public const string PlanReference = "docs/integration-module-plan.md §6";

    /// <summary>
    /// The supplier, named because the operator's next action is to ask them. SBS publishes
    /// Amplitude; the IMF's own IT department cannot answer either question below.
    /// </summary>
    public const string Supplier = "SBS";

    /// <summary>
    /// What is missing when we do not know which release the installation runs — the settings row
    /// could not be read, or carries another kind's settings. Both asks are named, because
    /// without the version we cannot tell which one is on the critical path.
    /// </summary>
    public const string MissingDocument =
        "the Amplitude interface contract has not been obtained from " + Supplier
        + " (neither the Up API service catalogue nor the batch file layout)";

    /// <summary>
    /// What is missing <b>for this installation</b>, in one sentence an operator can forward to
    /// whoever owns the SBS relationship.
    ///
    /// <para>
    /// Never a service name, never an endpoint path, never a record layout or a field: inventing
    /// any of them is the failure mode this whole type exists to prevent. The sentence says what
    /// KIND of artefact is wanted, which is exactly as much as we know.
    /// </para>
    /// </summary>
    /// <param name="version">
    /// The release configured on the connection, or <c>null</c> when it could not be read. Null
    /// does NOT default to one of the two: naming the wrong artefact would send a procurement
    /// conversation after a document that would not unblock this installation.
    /// </param>
    public static string MissingDocumentFor(AmplitudeVersion? version)
        => version switch
        {
            AmplitudeVersion.Up =>
                $"the Amplitude Up API service catalogue has not been obtained from {Supplier}",

            AmplitudeVersion.Legacy =>
                "the Amplitude batch file layout and its acknowledgement format have not been "
                + $"obtained from {Supplier}",

            _ => MissingDocument,
        };

    /// <summary>
    /// The five questions to settle, in the order they unblock work.
    ///
    /// <para>
    /// Ordered, and not a set. Question 1 decides which of 2 and 3/4 is on the critical path —
    /// and only that; it does not decide what gets built, because one SANKORE deployment serves
    /// both kinds of installation and both asks are eventually needed. Questions 3 and 4 are the
    /// pair <c>PerfectVisionSpecification</c> already learned to separate: a layout decides
    /// whether a file can be written at all, an acknowledgement format decides whether a written
    /// file can ever be CLOSED (a batch command stays <c>Batched</c> until an acknowledgement
    /// closes it — INT-24/INT-25, and it waits out <c>AckTimeoutHours</c> before alerting).
    /// Question 5 is last on purpose: an environment obtained before the contracts is an
    /// environment nobody can call.
    /// </para>
    ///
    /// <para>
    /// <b>2 and 3/4 are two separate asks of SBS</b>, not two halves of one. They are produced by
    /// different teams, they concern different releases of the product, and an answer to one says
    /// nothing about the other — which is why the health check and every refusal name only the
    /// one this installation needs.
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
        "1. Which Amplitude releases are in scope at the institutions we must serve, and whether "
        + "the API module is licensed and enabled on the Up ones. This decides which of the two "
        + "asks below is on the critical path; it does not remove either, because one SANKORE "
        + "deployment serves an Up tenant and a pre-Up tenant side by side, and "
        + "AmplitudeSettings.AmplitudeVersion is what tells them apart.",

        "2. Amplitude Up — the API service catalogue and its authentication: which services exist "
        + "for creating and updating a customer, opening an account, writing the KYC tier, "
        + "reading accounts, balances and loans, and submitting a loan application; the request "
        + "and response contract of each; how a call is authenticated; and whether a replayed "
        + "call can be made a no-op. That last point is not a detail: every write in this module "
        + "carries an IdempotencyKey precisely because a timeout cannot tell 'not created' from "
        + "'created, answer lost', and a service that ignores the key turns our retry into a "
        + "second customer. AmplitudeSettings.BaseUrl and CredentialVaultRef already carry the "
        + "coordinates and the vault reference — the catalogue is what no document here defines.",

        "3. Pre-Up — the outbound file layout and its encoding: which record types exist, which "
        + "fields each carries, in what order, fixed-width or delimited, and in which code page. "
        + "BatchCapableSettings already offers FileEncoding and FieldSeparator, so a delimited "
        + "file in a non-UTF-8 code page is configurable — but not guessable.",

        "4. Pre-Up — the acknowledgement format: what Amplitude writes back, under what file name, "
        + "in which directory, and how one acknowledgement line identifies the record it answers. "
        + "Without it a command can be sent and never closed, which is worse than refusing to "
        + "send it.",

        "5. A test environment, from the institution or from " + Supplier + ", and whether one set "
        + "of credentials covers both carriers. This is what INT-31's criterion 4 asks for, and it "
        + "verifies work that questions 2 to 4 make possible — obtained first, it is an "
        + "environment with nothing to send it.",
    ];

    /// <summary>
    /// The <c>Detail</c> every refusal carries. Names the operation so a call-journal row is
    /// readable on its own, then the missing document for this installation's release, then where
    /// the block is recorded.
    /// </summary>
    /// <param name="operation">
    /// The logical operation, from <see cref="AmplitudeOperations"/> — a constant and not a
    /// literal, so that the word in this message is the same word the capability matrix uses.
    /// </param>
    /// <param name="version">The configured release, or <c>null</c> when it could not be read.</param>
    public static string RefusalDetail(string operation, AmplitudeVersion? version)
        => $"Amplitude cannot serve {operation}: {MissingDocumentFor(version)}. "
           + $"This is a supplier dependency and not a defect — see {PlanReference}. "
           + "Writing a plausible mapping without the contract would produce an adapter that "
           + "reports writes the core banking system never accepted.";

    /// <summary>
    /// The same statement for the health check, which answers an operator staring at an
    /// activation screen rather than a queued command.
    /// </summary>
    public static string HealthDetail(AmplitudeVersion? version)
        => $"Amplitude is not operable: {MissingDocumentFor(version)}. See {PlanReference}. "
           + "The connection may be configured and will stay inactive until the contract arrives.";
}

/// <summary>
/// The logical operation names, as constants.
///
/// <para>
/// Same reason as <c>TemenosOperations</c> and <c>PerfectVisionOperations</c>: the word appears in
/// a refusal detail and in the capability matrix, and a typo would give an administrator reading
/// the matrix and an operator reading a rejection two different vocabularies for one operation.
/// They match the <see cref="IntegrationCapability"/> names exactly.
/// </para>
/// </summary>
public static class AmplitudeOperations
{
    public const string CheckHealth = "CheckHealth";
    public const string CreateCustomer = "CreateCustomer";
    public const string UpdateCustomer = "UpdateCustomer";
    public const string SetKycLevel = "SetKycLevel";
    public const string OpenAccount = "OpenAccount";
    public const string ReadAccounts = "ReadAccounts";
    public const string ReadBalance = "ReadBalance";
    public const string SubmitLoanApplication = "SubmitLoanApplication";
    public const string ReadLoans = "ReadLoans";
    public const string DebitAccount = "DebitAccount";
    public const string ReverseDebit = "ReverseDebit";
}
