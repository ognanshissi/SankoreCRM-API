namespace Sankore.Modules.Leads.Tests.Retention;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Infrastructure.Retention;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Retention;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The nightly technical purges run ExecuteDelete/ExecuteUpdate, which the InMemory provider cannot
/// execute. These tests run them against the REAL provider pointed at a port nothing listens on:
/// a query EF cannot translate throws before any connection, so failing ON the connection
/// (an <c>NpgsqlException</c> somewhere in the chain) is the proof the purge translates.
/// </summary>
public sealed class TechnicalPurgeSqlTranslationTests
{
    private static LeadsDbContext Unreachable() => new LeadsDbContext(
        new DbContextOptionsBuilder<LeadsDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Timeout=1;Database=none;Username=none;Password=none")
            .UseSnakeCaseNamingConvention()
            .Options,
        new FixedTenantContext(Guid.NewGuid()), LeadsTestEncryptor.Instance);

    private static async Task ShouldTranslate(ITechnicalPurge purge)
    {
        var thrown = await Record.ExceptionAsync(() => purge.PurgeAsync(DateTimeOffset.UtcNow, default));
        thrown.Should().NotBeNull("nothing listens on that port");

        // Npgsql wraps the refused connection in « a transient failure » InvalidOperationException,
        // so the type alone proves nothing: the connection error must be in the chain.
        var reachedTheNetwork = false;
        for (var e = thrown; e is not null; e = e.InnerException)
            reachedTheNetwork |= e is Npgsql.NpgsqlException;
        reachedTheNetwork.Should().BeTrue(thrown!.ToString());
    }

    [Fact]
    public Task Lead_import_files_translates() => ShouldTranslate(new LeadImportFilesPurge(Unreachable(), Substitute.For<IFileStore>(), NullLogger<LeadImportFilesPurge>.Instance));

    [Fact]
    public Task Raw_ingestions_translates() => ShouldTranslate(new RawIngestionsPurge(Unreachable()));
}
