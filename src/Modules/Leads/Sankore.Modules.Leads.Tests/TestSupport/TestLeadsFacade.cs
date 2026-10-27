namespace Sankore.Modules.Leads.Tests.TestSupport;

using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;

/// <summary>
/// Builds <see cref="LeadsModuleFacade"/> with the three collaborators a suite that is not
/// testing them does not want to think about.
///
/// <para>
/// Here rather than repeated per file because the facade gained a dependency once already
/// (TEL-B-06's ownership check) and broke two unrelated suites doing so. The defaults are
/// deliberately inert: a substituted <c>IPhoneBlindIndexer</c> that returns the same string for
/// every number would make a lookup test pass vacuously, so a suite that tests one passes its
/// own.
/// </para>
/// </summary>
internal static class TestLeadsFacade
{
    internal static LeadsModuleFacade Build(
        LeadsDbContext db,
        IPhoneBlindIndexer? indexer = null,
        Guid? actorUserId = null,
        Guid? tenantId = null,
        IReadOnlyList<string>? roles = null,
        ILeadEmailIndexer? emailIndexer = null)
    {
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.Id.Returns(actorUserId ?? Guid.NewGuid());
        currentUser.TenantId.Returns(tenantId ?? Guid.NewGuid());
        currentUser.IsAuthenticated.Returns(true);

        // Empty by default and not a bypass role: LeadAccessGuard lets System, Administrator and
        // SalesManager through unconditionally, so a default of "Administrator" would make every
        // ownership assertion pass without the lead being owned by anybody.
        currentUser.Roles.Returns(roles ?? []);

        var administration = Substitute.For<IAdministrationModule>();
        administration.GetTeamAgentIdsAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns([]);

        return new LeadsModuleFacade(
            db,
            indexer ?? Substitute.For<IPhoneBlindIndexer>(),
            currentUser,
            new LeadAccessGuard(currentUser, administration),
            emailIndexer: emailIndexer);
    }
}
