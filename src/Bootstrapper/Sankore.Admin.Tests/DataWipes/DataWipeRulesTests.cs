namespace Sankore.Admin.Tests.DataWipes;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Domain;
using Sankore.Admin.Features.Tenants.DataWipes;
using Sankore.Admin.Tests.Modules;
using Xunit;

/// <summary>
/// Emptying a TEST institution (data-purge lot 4): only a test one, only with its name retyped,
/// one at a time.
/// </summary>
public sealed class DataWipeRulesTests : IDisposable
{
    private readonly ModuleTestDb _db = new();

    public void Dispose() => _db.Dispose();

    private async Task MarkAsTestAsync()
    {
        await using var db = _db.Create();
        (await db.Tenants.SingleAsync(t => t.Id == _db.TenantId)).MarkAsTest(true);
        await db.SaveChangesAsync();
    }

    private async Task<(string? Error, TenantDataWipe? Wipe)> RequestAsync(string confirmation)
    {
        await using var db = _db.Create();
        return await DataWipeRules.RequestAsync(db, _db.TenantId, confirmation, Guid.NewGuid(), DateTimeOffset.UtcNow, default);
    }

    [Fact]
    public async Task A_production_institution_is_never_wiped()
        => (await RequestAsync("Caisse Avenir")).Error.Should().Be(DataWipeEndpoints.NotTest);

    [Fact]
    public async Task The_name_must_be_retyped_exactly()
    {
        await MarkAsTestAsync();

        (await RequestAsync("caisse avenir")).Error.Should().Be(DataWipeEndpoints.ConfirmationMismatch);
        (await RequestAsync("  Caisse Avenir ")).Error.Should().BeNull();
    }

    [Fact]
    public async Task One_wipe_at_a_time()
    {
        await MarkAsTestAsync();
        (await RequestAsync("Caisse Avenir")).Wipe!.Status.Should().Be(TenantDataWipeStatus.Pending);

        (await RequestAsync("Caisse Avenir")).Error.Should().Be(DataWipeEndpoints.InFlight);
    }
}
