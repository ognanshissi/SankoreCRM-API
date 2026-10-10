namespace Sankore.Modules.Leads.Tests.TestSupport;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Leads.Features.DispatchLead;

/// <summary>
/// Builders for <see cref="ClientAdvisorResolver"/> in dispatch tests.
///
/// <para>
/// The real resolver over a substituted <see cref="ICustomersModule"/>, not a stub of the resolver
/// itself: the rule being tested — "route to the client's advisor only when that advisor is in the
/// eligible pool" — lives inside it, and a stub would assert nothing about it.
/// </para>
/// </summary>
internal static class TestClientAdvisorResolver
{
    /// <summary>
    /// A resolver that never finds an advisor, so dispatching falls through to the configured
    /// strategy. The right default for every test written before BNF-B-22: those leads carry no
    /// CustomerId, and the resolver returns null without calling M01 at all.
    /// </summary>
    internal static ClientAdvisorResolver None() =>
        new(Substitute.For<ICustomersModule>(), NullLogger<ClientAdvisorResolver>.Instance);

    /// <summary>A resolver whose M01 reports <paramref name="advisorUserId"/> for that client.</summary>
    internal static ClientAdvisorResolver WithAdvisor(
        Guid tenantId, Guid clientId, Guid? advisorUserId)
    {
        var customers = Substitute.For<ICustomersModule>();
        customers.GetClientSummaryAsync(tenantId, clientId, Arg.Any<CancellationToken>())
            .Returns(new ClientSummary(
                Id:            clientId,
                ClientNumber:  "CLI-0001",
                ClientType:    "Individual",
                DisplayName:   "Client Test",
                Status:        "Active",
                AgencyId:      Guid.NewGuid(),
                AdvisorUserId: advisorUserId,
                KycStatus:     "Approved",
                RiskLevel:     "Low",
                MergedIntoId:  null));

        return new ClientAdvisorResolver(customers, NullLogger<ClientAdvisorResolver>.Instance);
    }
}
