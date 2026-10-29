namespace Sankore.Modules.Integration.Tests.Features.ConnectedAccounts;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.ConnectedAccounts;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The Messagerie list translates to PostgreSQL — which the InMemory suite cannot tell: it
/// evaluates a GROUP BY with a correlated EXISTS, or a filter composed on a projection, in memory
/// without complaining, and Npgsql may refuse the same shape in production. No connection is
/// opened: <c>ToQueryString</c> compiles the query, the step that throws.
/// </summary>
public sealed class MailThreadsSqlTranslationTests
{
    private static IntegrationDbContext NpgsqlContext() => new(
        new DbContextOptionsBuilder<IntegrationDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=none;Password=none")
            .UseSnakeCaseNamingConvention()
            .Options,
        new FixedTenantContext(Guid.NewGuid()));

    [Fact]
    public void A_tab_of_a_searched_mailbox_translates_with_its_page()
    {
        using var db = NpgsqlContext();
        var me = Guid.NewGuid();
        List<string> keys = ["thread-1", "thread-2"];

        var messages = db.ConnectedMailMessages.Where(m => m.UserId == me).Where(m => keys.Contains(m.ThreadKey));
        var grouped = ListMyMailThreadsHandler.Group(ListMyMailThreadsHandler.WithLink(db, messages, MailLinkEntity.Lead))
            .Where(g => g.Unread > 0);

        var sql = grouped.OrderByDescending(g => g.LastAt).ThenBy(g => g.ThreadKey).Skip(25).Take(25).ToQueryString();

        sql.Should().Contain("GROUP BY").And.Contain("EXISTS").And.Contain("HAVING");
    }
}
