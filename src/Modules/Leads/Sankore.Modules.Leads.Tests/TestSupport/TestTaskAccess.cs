namespace Sankore.Modules.Leads.Tests.TestSupport;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Features.Tasks.Collaboration;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;

/// <summary>A <see cref="TaskAccess"/> for suites not testing it: an administrator, who reaches every task.</summary>
internal static class TestTaskAccess
{
    internal static TaskAccess Administrator(LeadsDbContext db)
    {
        var user = Substitute.For<ICurrentUser>();
        user.Id.Returns(Guid.NewGuid());
        user.Roles.Returns(["Administrator"]);
        return new TaskAccess(db, user, Substitute.For<IAdministrationModule>(), NullLogger<TaskAccess>.Instance);
    }
}
