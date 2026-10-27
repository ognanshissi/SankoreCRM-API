namespace Sankore.Modules.Leads.Tests.Features.QualificationTemplates;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.QualificationTemplates.PublishQualificationTemplate;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Un formulaire publié par (tenant, famille, produit) — propriétaire, 2026-10-27 : une famille en
/// porte un par produit, plus le sien ; publier ne retire que celui du même produit.
/// </summary>
public sealed class PublishedScopeTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;

    public PublishedScopeTests() => _factory = new TestDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Two_products_of_one_family_each_keep_their_published_form()
    {
        var auto = await PublishNewAsync(ProductCategory.Loan, "PRET_AUTO");
        var immo = await PublishNewAsync(ProductCategory.Loan, "PRET_IMMO");
        var family = await PublishNewAsync(ProductCategory.Loan, null);

        (await StatusAsync(auto)).Should().Be(TemplateStatus.Published);
        (await StatusAsync(immo)).Should().Be(TemplateStatus.Published);
        (await StatusAsync(family)).Should().Be(TemplateStatus.Published);
    }

    [Fact]
    public async Task Publishing_a_product_form_archives_only_the_previous_one_of_that_product()
    {
        var first = await PublishNewAsync(ProductCategory.Loan, "PRET_AUTO");
        var family = await PublishNewAsync(ProductCategory.Loan, null);

        var second = await PublishNewAsync(ProductCategory.Loan, "PRET_AUTO");

        (await StatusAsync(first)).Should().Be(TemplateStatus.Archived);
        (await StatusAsync(second)).Should().Be(TemplateStatus.Published);
        (await StatusAsync(family)).Should().Be(TemplateStatus.Published);
    }

    [Fact]
    public async Task The_family_form_replaces_the_previous_family_form_only()
    {
        var product = await PublishNewAsync(ProductCategory.Loan, "PRET_AUTO");
        var first = await PublishNewAsync(ProductCategory.Loan, null);

        var second = await PublishNewAsync(ProductCategory.Loan, null);

        (await StatusAsync(first)).Should().Be(TemplateStatus.Archived);
        (await StatusAsync(second)).Should().Be(TemplateStatus.Published);
        (await StatusAsync(product)).Should().Be(TemplateStatus.Published);
    }

    private async Task<Guid> PublishNewAsync(ProductCategory category, string? code)
    {
        var template = QualificationTemplate.Create(_tenantId, "Entretien", DateTimeOffset.UtcNow, null, category, code);
        template.AddQuestion("Revenu mensuel ?", QuestionType.Numeric, 10, false).IsSuccess.Should().BeTrue();

        await using (var db = _factory.CreateContext())
        {
            db.QualificationTemplates.Add(template);
            await db.SaveChangesAsync();
        }

        await using (var db = _factory.CreateContext())
        {
            var result = await new PublishQualificationTemplateHandler(db, TimeProvider.System).Handle(
                new PublishQualificationTemplateCommand(template.Id), CancellationToken.None);
            result.IsSuccess.Should().BeTrue(result.Error);
        }

        return template.Id;
    }

    private async Task<TemplateStatus> StatusAsync(Guid id)
    {
        await using var db = _factory.CreateContext();
        return (await db.QualificationTemplates.SingleAsync(t => t.Id == id)).Status;
    }
}
