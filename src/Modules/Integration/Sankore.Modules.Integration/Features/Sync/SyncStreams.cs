namespace Sankore.Modules.Integration.Features.Sync;

using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// Which streams belong to which family of connector (INT-20, criterion 1).
///
/// <para>
/// <see cref="SyncStream"/> is one enum covering both families because the cursor table and the
/// orchestrator are shared, but a connection only ever owns the streams of its own family: asking
/// a core banking system for <see cref="SyncStream.Claims"/> is not an empty answer, it is a call
/// that cannot be made. The mapping lives here rather than on the enum so the Domain folder —
/// owned by other work in flight — is not touched.
/// </para>
/// </summary>
internal static class SyncStreams
{
    private static readonly SyncStream[] CoreBankingStreams =
        [SyncStream.Customers, SyncStream.Accounts, SyncStream.Transactions, SyncStream.Loans];

    private static readonly SyncStream[] InsuranceStreams =
        [SyncStream.Policies, SyncStream.Claims];

    /// <summary>
    /// The streams a connection of this family is swept on.
    ///
    /// <para>
    /// A family with no case here <b>throws</b> rather than returning an empty list. An empty list
    /// would leave every connection of a newly added family permanently unsynchronised with
    /// nothing anywhere saying so; the throw surfaces in the orchestrator's per-connection log the
    /// first minute after the deployment.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<SyncStream> For(IntegrationFamily family) => family switch
    {
        IntegrationFamily.CoreBanking => CoreBankingStreams,
        IntegrationFamily.Insurance => InsuranceStreams,
        _ => throw new ArgumentOutOfRangeException(
            nameof(family), family,
            "No sync streams are declared for this integration family. Declare them in "
            + "SyncStreams rather than leaving the family unswept."),
    };
}
