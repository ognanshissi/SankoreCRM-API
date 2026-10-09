namespace Sankore.Modules.Integration.Features.Balance;

/// <summary>
/// The one code this area answers with.
///
/// <para>
/// Declared here and not in <c>IntegrationErrors</c> deliberately: that catalogue is in the
/// PublicApi because consumer modules and adapters branch on its codes, and nothing outside this
/// slice branches on this one. It is an HTTP-side answer, like <c>CLIENT_NOT_FOUND</c> in M01.
/// </para>
/// </summary>
internal static class BalanceErrors
{
    /// <summary>
    /// <b>One code for four different situations</b>, and that is the point: the customer does not
    /// exist, the customer lies outside the caller's agency perimeter, the account is not the
    /// customer's, or neither the CBS nor the snapshot holds a figure for it. All four answer 404
    /// with this code.
    ///
    /// <para>
    /// Distinguishing them would hand an agent a probe: a different answer for "outside your
    /// perimeter" than for "does not exist" confirms the existence of clients and accounts in
    /// other branches, which is exactly what the perimeter compartmentalises. Same rule, same
    /// reason, as M01's <c>CLIENT_NOT_FOUND</c> and M02's file read.
    /// </para>
    /// </summary>
    internal const string BalanceNotAvailable = "INTEGRATION_BALANCE_NOT_AVAILABLE";
}
