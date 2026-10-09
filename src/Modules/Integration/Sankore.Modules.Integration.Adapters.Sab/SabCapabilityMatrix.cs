namespace Sankore.Modules.Integration.Adapters.Sab;

using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// What a SAB AT installation reached through Open SAB will serve, and in which mode (INT-32).
///
/// <para>
/// <b>Deliverable without SBS, and the part a screen depends on.</b> The matrix does not describe
/// a wire format; it describes the shape of the integration — Open SAB is an HTTP API in front of
/// a full core banking ledger, so its operations are synchronous and its reads are answerable.
/// That is knowable from the product without the catalogue, which is why this file carries no
/// refusal. Unlike Perfect Vision (INT-28), nothing here is batch: <c>SabSettings</c> is not a
/// <c>BatchCapableSettings</c>, so a SAB connection cannot even be configured in
/// <c>IntegrationMode.Batch</c> — <c>ConnectionSettingsValidator</c> refuses it — and there is no
/// file cycle for a command to wait in.
/// </para>
///
/// <para>
/// A pure function of the settings, so the matrix of a properly scoped installation and the matrix
/// of one with no entity can be asserted side by side with no database in the way. The adapter's
/// <c>Capabilities</c> property is this function applied to the tenant's own connection.
/// </para>
/// </summary>
public static class SabCapabilityMatrix
{
    /// <summary>
    /// The operations INT-32 brings into scope, all served live.
    ///
    /// <para>
    /// <see cref="CapabilityMode.RealTime"/> throughout, and the mode is the thing in this file
    /// most worth getting right: it is a statement about the carrier. <c>IntegrationModuleFacade</c>
    /// reads <c>IsRealTime(ReadBalance)</c> and goes STRAIGHT to the INT-21 snapshot when it is
    /// false, so declaring <see cref="CapabilityMode.Batch"/> for a live HTTP read would silently
    /// serve a counter a figure from last night's sweep while an installation answered in the
    /// second. In the other direction the error is worse: on a batch carrier,
    /// <c>RealTime</c> makes the platform wait for a synchronous answer nothing ever produces.
    /// Open SAB answers synchronously, so <c>RealTime</c> is the honest mode for every entry.
    /// </para>
    ///
    /// <para>
    /// <b>Product or installation?</b> The MODE is a property of the product — Open SAB is HTTP,
    /// on every installation. The LIST is a property of the installation: which services SBS
    /// publishes, and which of them the IMF's licence covers, is precisely what the catalogue
    /// would say (question 1 and question 3 of <see cref="SabSpecification.OpenQuestions"/>), and
    /// nothing in a settings row tells us. So this list is what a SAB AT installation is expected
    /// to serve, and the narrowing per installation is the one thing <see cref="For"/> can read.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<IntegrationCapability> LiveOperations =
    [
        // INT-12 — customers and the KYC tier we push.
        IntegrationCapability.CreateCustomer,
        IntegrationCapability.UpdateCustomer,
        IntegrationCapability.SetKycLevel,

        // INT-13 / INT-15 — accounts and the live balance a counter reads.
        IntegrationCapability.OpenAccount,
        IntegrationCapability.ReadAccounts,
        IntegrationCapability.ReadBalance,

        // INT-13 / INT-22 — history and the monthly flow the simplified-KYC ceilings watch.
        IntegrationCapability.ReadTransactions,
        IntegrationCapability.ReadMonthlyFlow,

        // Loans.
        IntegrationCapability.SubmitLoanApplication,
        IntegrationCapability.ReadLoans,
    ];

    /// <summary>
    /// The matrix for one installation.
    ///
    /// <para>
    /// <b>Nothing at all when the connection names no entity.</b> That is criterion 2 showing up
    /// in the matrix rather than only on the call path: an installation this deployment cannot
    /// scope to an institution is an installation no operation can be attempted against, because
    /// every port method refuses on the entity guard first (see <see cref="SabEntityScope"/>).
    /// Declaring capabilities anyway would offer a screen ten buttons whose every press answers a
    /// configuration error — and it would do so for the one connection where the honest answer
    /// matters most, since the reason the call is refused is that it might otherwise reach another
    /// institution's data.
    /// </para>
    ///
    /// <para>
    /// <b>What is absent and why.</b> <see cref="IntegrationCapability.ReadKycLevel"/> is not
    /// declared and <c>ICbsKycLevelPort</c> is not implemented: whether Open SAB exposes a
    /// customer's tier as something one can read back is question 3, and the port's own contract
    /// says an adapter implementing it MUST declare the capability — so claiming it would have
    /// INT-21 compute a compliance divergence from a figure that cannot be obtained. Note that
    /// this absence means something different from the same absence in Perfect Vision's matrix:
    /// there it says "never" (a batch-file CBS answers no query at all), here it says "not until
    /// the catalogue says the service exists". The matrix cannot express that difference, which is
    /// why this comment has to. <see cref="IntegrationCapability.DebitAccount"/> and
    /// <see cref="IntegrationCapability.ReverseDebit"/> are absent because they belong to ASS-05
    /// and are not in INT-32's criteria — the same position <c>TemenosAdapter</c> takes, and the
    /// two methods exist because the port demands them and refuse as not supported. An absent
    /// capability is refused by <c>IntegrationAdapterResolver.ResolvePort</c> before any call is
    /// made, which is a stronger guarantee than a method that says no. The insurance capabilities
    /// are not this family's at all.
    /// </para>
    ///
    /// <para>
    /// <b>Why anything is declared when every call refuses today.</b> The matrix answers "what
    /// will this installation serve", which is what a screen needs in order to decide which
    /// buttons exist at all; the refusal answers "with what we hold today". Keeping the list
    /// declared is what makes the arrival of the catalogue a generated client and a file of field
    /// mappings rather than a change of architecture — and no button is offered that cannot be
    /// served, because a SAB connection cannot be activated in the first place (see
    /// <c>SabAdapter.CheckHealthAsync</c>) and <c>IntegrationModuleFacade.GetCapabilities</c>
    /// resolves the tenant's ACTIVE core banking connection, so a tenant whose only SAB row is
    /// inactive reads <see cref="IntegrationCapabilities.None"/> from the facade regardless of
    /// what this function returns.
    /// </para>
    /// </summary>
    /// <param name="settings">
    /// The connection's settings, or <c>null</c> when none could be read — no connection, or a SAB
    /// row carrying another kind's settings. Null narrows the matrix to
    /// <see cref="IntegrationCapabilities.None"/>, because every entry here depends on being able
    /// to scope the call and unreadable settings scope nothing. Narrowing on unreadable
    /// configuration is the only safe direction: a matrix that widened would offer a live customer
    /// write to an installation this deployment cannot address.
    /// </param>
    public static IntegrationCapabilities For(SabSettings? settings)
    {
        if (SabEntityScope.ResolveFor(settings) != SabEntityResolution.Resolved)
            return IntegrationCapabilities.None;

        return IntegrationCapabilities.All(CapabilityMode.RealTime, [.. LiveOperations]);
    }
}
