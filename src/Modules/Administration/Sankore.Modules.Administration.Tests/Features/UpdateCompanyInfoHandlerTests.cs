namespace Sankore.Modules.Administration.Tests.Features;

using FluentAssertions;
using Sankore.Modules.Administration.Features.CompanyInfo.UpdateCompanyInfo;
using Sankore.Modules.Administration.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;
using CompanyInfoEntity = Sankore.Modules.Administration.Domain.CompanyInfo;

/// <summary>
/// The institution's update is SAVED (2026-11-10). AdministrationDbContext reads without tracking
/// by default, and the handler answered success while writing nothing: the colours chosen came
/// back to the default at every reload.
/// </summary>
public sealed class UpdateCompanyInfoHandlerTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _factory;

    public UpdateCompanyInfoHandlerTests() => _factory = new TestAdminDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Name_colours_language_and_currency_are_saved()
    {
        await using (var seed = _factory.CreateContext())
        {
            seed.CompanyInfos.Add(CompanyInfoEntity.Create(_tenant, "Avant", "Description"));
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        await using (var db = _factory.CreateContext())
            (await new UpdateCompanyInfoHandler(db).Handle(new UpdateCompanyInfoCommand(
                "Après", "Nouvelle description", "logo.png", "#7A1F5C", "#103456", Languages.En, "EUR"), CancellationToken.None))
                .IsSuccess.Should().BeTrue();

        await using var read = _factory.CreateContext();
        var saved = read.CompanyInfos.Single();
        saved.Name.Should().Be("Après");
        saved.PrimaryColor.Should().Be("#7A1F5C");
        saved.SecondaryColor.Should().Be("#103456");
        saved.DefaultLanguage.Should().Be(Languages.En);
        saved.Currency.Should().Be("EUR");
    }

    [Fact]
    public async Task Empty_fields_sent_as_null_are_saved_empty_and_colours_kept()
    {
        await using (var seed = _factory.CreateContext())
        {
            seed.CompanyInfos.Add(CompanyInfoEntity.Create(_tenant, "Avant", "Description"));
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        await using (var db = _factory.CreateContext())
            (await new UpdateCompanyInfoHandler(db).Handle(new UpdateCompanyInfoCommand(
                "Après", null!, null!, null!, null!, Languages.Fr), CancellationToken.None))
                .IsSuccess.Should().BeTrue();

        await using var read = _factory.CreateContext();
        var saved = read.CompanyInfos.Single();
        saved.Description.Should().BeEmpty();
        saved.LogoUrl.Should().BeEmpty();
        saved.PrimaryColor.Should().Be("#1A4D4DFF", "an empty colour is not a choice");
    }
}
