namespace Sankore.Modules.Integration.Adapters.Ringover;

/// <summary>
/// The missing prerequisites, named once (TEL-B-04).
///
/// <para>
/// A type rather than a comment, and one place rather than ten literals, because this string is
/// the only thing an operator will ever see from this adapter. It reaches them through the
/// <c>Detail</c> of an <c>IntegrationResult</c> — a refused click-to-call, a failed health check
/// — and it has to tell them the truth: nothing is broken, two prerequisites have not arrived,
/// and no change in this repository can lift them.
/// </para>
///
/// <para>
/// <b>TWO PREREQUISITES, AND ONLY ONE OF THEM IS A DOCUMENT.</b> That is the difference from the
/// four core-banking and insurance precedents, which each wait on a vendor deliverable and
/// nothing else. TEL-B-04 is conditioned on <b>a test account</b> and on <b>the six open points
/// below</b>. The account is not paperwork around the document: a telephony adapter cannot be
/// written against a description, because what has to be right is a vocabulary of statuses and
/// error bodies that only a live account produces — and the contract suite of TEL-B-21 asks for
/// exactly that, green against the real provider.
/// </para>
///
/// <para>
/// <b>The spec's own fallbacks apply by default and are implemented</b>, which is the other
/// difference: this adapter is not idle while it waits. Three of the six points have a stated
/// fallback, the fallback is what the module already does, and the open point is whether the
/// provider offers something better — see <see cref="RingoverFallbacks"/>. So the answers can
/// only ever IMPROVE the behaviour; none of them is load-bearing for the feature to work.
/// </para>
///
/// <para>
/// <b>What this type is NOT allowed to contain</b>, now or ever: an endpoint path, a verb, a
/// header name, a field name, a status token, an error code of the provider's. An invented
/// identifier in a message is how an invented identifier reaches a translator — somebody reads
/// the error, takes the name for documentation, and the guess becomes the contract. A test pins
/// the absence. The words that DO appear — Ringover, click-to-call, webhook — are the user
/// story's own and the settings record's own, not ours.
/// </para>
/// </summary>
public static class RingoverSpecification
{
    /// <summary>Where the block is recorded.</summary>
    public const string PlanReference = "the telephony plan, lot 7 (TEL-B-04)";

    /// <summary>The provider.</summary>
    public const string Supplier = "Ringover";

    /// <summary>
    /// Who the operator's next action is with. Both halves, in one phrase, because the account is
    /// obtained from the provider while the six points are settled with them — a message naming
    /// only "the documentation" would have somebody read a public page and conclude the block was
    /// lifted.
    /// </summary>
    public const string Counterparty = Supplier + " (a test account, and their answers)";

    /// <summary>
    /// What is missing, in one sentence an operator can forward.
    ///
    /// <para>
    /// Never an endpoint, never a field, never a status token: inventing any of them is the
    /// failure mode this whole type exists to prevent. The sentence says what KIND of thing is
    /// wanted, which is exactly as much as is known.
    /// </para>
    /// </summary>
    public const string MissingPrerequisites =
        "no " + Supplier + " test account has been obtained, so the provider's own vocabulary — "
        + "its call statuses, its error bodies, its webhook authentication — has never been "
        + "observed, and the six open points of the specification are unsettled";

    /// <summary>
    /// The six questions to settle, in the order they unblock work.
    ///
    /// <para>
    /// Ordered, and not a set. Question 1 is first because it is the only one that cannot be
    /// answered from a document: the account IS the answer to it, and the five below are answered
    /// against the account rather than before it. Questions 2 and 3 are the adapter's own work —
    /// the status vocabulary and the error bodies — and they are the two that a guess would
    /// corrupt silently. Questions 4 to 6 each have a FALLBACK already implemented, so they
    /// improve behaviour rather than enable it, which is why they come last.
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
        "1. A test account on the provider's side, with at least two users, a number that can "
        + "receive calls and one that can place them. It is the prerequisite TEL-B-04 names and it "
        + "is not a document: TEL-B-21 asks for green contract tests against the real provider, "
        + "and the four points below are observations about live traffic rather than readings of a "
        + "page. One user is not enough — the attribution of a call to an agent and the exclusion "
        + "of colleague-to-colleague calls from the indicators (TEL-B-19) are both unobservable "
        + "with a single handset.",

        "2. The call-status vocabulary: every token the provider can report for a call, and which "
        + "of SANKORE's six statuses each one means. This is the point a guess would corrupt "
        + "silently, so nothing is guessed — RingoverStatusTranslation ships with an EMPTY table "
        + "and an unknown token leaves the call on its last known status, which is the behaviour "
        + "the plan asks for. Two sub-questions that a list of tokens alone does not answer: "
        + "whether the provider distinguishes 'rang and nobody answered' from 'went to the "
        + "mailbox' (SANKORE does, because they are different next actions for an agent), and "
        + "whether it reports a colleague-to-colleague call as internal at all — without which "
        + "TEL-B-19's exclusion has nothing to exclude, and degrades to counting those calls, "
        + "which is stated as the designed fallback rather than discovered later.",

        "3. The error bodies: what the provider returns when a number is invalid, when an agent's "
        + "handset is unreachable, and when a rate limit is hit. The HTTP axis needs no answer and "
        + "is implemented — RingoverErrorClassification maps 401/403 to Technical, 429 and 5xx to "
        + "Transient, and a refusal on the content of the request to Functional, which is the "
        + "plan's own classification. What is open is narrower and still matters: whether an "
        + "invalid number is refused with a 4xx at all or accepted and failed later on a webhook, "
        + "because the first is a message to the agent before their telephone rings and the second "
        + "is a journal row they have to go and read.",

        "4. Webhook authenticity — whether the provider signs its webhooks, and over what. "
        + "FALLBACK ALREADY IMPLEMENTED: a per-connection URL token plus a re-read of the call "
        + "through the API, so nothing in a webhook body is ever trusted (the journal re-reads the "
        + "call whatever the event said). A signature would let the verifier reject a forged body "
        + "before the re-read, which is cheaper; it would not make the data more trustworthy, "
        + "because it is already not trusted.",

        "5. A click-to-call reference — whether the provider returns, on the call it places, an "
        + "identifier that later appears on the call itself. FALLBACK ALREADY IMPLEMENTED: "
        + "correlation by (agent, number, two minutes), and ambiguity measured in AGENTS rather "
        + "than in requests, so two colleagues ringing the same shop within the window attach to "
        + "neither. A reference would make the correlation exact and retire the window.",

        "6. Recording deletion at the provider — whether the API can delete its own copy once "
        + "SANKORE holds an encrypted one, and whether a deletion is auditable on their side. "
        + "FALLBACK ALREADY IMPLEMENTED: purge on the SANKORE side only, and the capability is "
        + "simply not declared, so the retention job never asks. The consequence has to be stated "
        + "to whoever answers rather than discovered by a compliance officer: without it, a "
        + "recording destroyed here still exists at the provider, so SANKORE's retention is a "
        + "statement about SANKORE and not about the conversation. Plus the two points the plan "
        + "refuses to assume anything about: where the provider stores its data, and the API's "
        + "rate limits — both to be confirmed contractually, and the module applies its own "
        + "per-tenant limiting meanwhile.",
    ];

    /// <summary>
    /// The <c>Detail</c> every specification refusal carries. Names the operation so a journal row
    /// is readable on its own, then the missing prerequisites, then where the block is recorded.
    /// </summary>
    /// <param name="operation">
    /// The logical operation, from <see cref="RingoverOperations"/> — a constant and not a
    /// literal, so the word in this message is the same word the capability matrix uses.
    /// </param>
    public static string RefusalDetail(string operation)
        => $"Ringover cannot serve {operation}: {MissingPrerequisites}. This is a supplier "
           + $"dependency and not a defect — forward it to whoever owns the {Counterparty} "
           + $"relationship, and see {PlanReference}. Writing a plausible status translation "
           + "without the account would produce an adapter that reports a conversation as missed "
           + "and a missed call as a conversation, which is worse than refusing: an agent would "
           + "stop calling somebody back, and a lead's first-contact clock would be stopped by a "
           + "call nobody had.";

    /// <summary>
    /// The same statement for the health check, which answers an operator staring at an
    /// activation screen rather than a refused command.
    /// </summary>
    public static string HealthDetail()
        => $"Ringover is not operable: {MissingPrerequisites}. See {PlanReference}. The connection "
           + "may be configured and will stay inactive until a test account is obtained. "
           + "Meanwhile the telephony family works end to end against the in-memory adapter, "
           + "which is what every lot before this one was built and verified on.";
}

/// <summary>
/// The logical operation names, as constants.
///
/// <para>
/// Same reason as <c>TemenosOperations</c>, <c>SabOperations</c> and <c>OrassOperations</c>: the
/// word appears in a refusal detail and in the capability matrix, and a typo would give an
/// administrator reading the matrix and an operator reading a rejection two different
/// vocabularies for one operation.
/// </para>
///
/// <para>
/// <see cref="ParseWebhook"/> is named here although it never refuses: it is the one port member
/// that is genuinely implemented — see <c>RingoverAdapter.ParseWebhook</c> — and naming it keeps
/// the list of operations the same length as the port.
/// </para>
/// </summary>
public static class RingoverOperations
{
    public const string CheckHealth = "CheckHealth";
    public const string ListUsers = "ListUsers";
    public const string StartCallback = "StartCallback";
    public const string GetCall = "GetCall";
    public const string ListCalls = "ListCalls";
    public const string DownloadRecording = "DownloadRecording";
    public const string DeleteRecording = "DeleteRecording";
    public const string ParseWebhook = "ParseWebhook";
}
