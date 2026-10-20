namespace Sankore.Modules.Administration.Tests.Features.Products;

using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Domain.Forms;
using Sankore.Modules.Administration.Features.Products.GetProduct;
using Sankore.Modules.Administration.Features.Products.UpdateProduct;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.PublicApi.Forms;
using Sankore.Modules.Administration.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

public sealed class UpdateProductHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _db;

    public UpdateProductHandlerTests() => _db = new TestAdminDbContextFactory(_tenantId);

    public void Dispose() => _db.Dispose();

    private async Task<Guid> ProductAsync(bool linked = true)
    {
        await using var db = _db.CreateContext();
        var product = ProductSpeciality.Create(_tenantId, "Crédit commerçant", "CREDCOM", ProductCategory.Loan);
        if (linked) product.LinkToCbs("SAB", "P-0042");
        db.ProductSpecialities.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private async Task<Result> UpdateAsync(UpdateProductCommand cmd)
    {
        await using var db = _db.CreateContext();
        return await new UpdateProductHandler(db, new ProductCatalogCache()).Handle(cmd, default);
    }

    [Fact]
    public async Task Saving_the_identity_tab_keeps_the_core_banking_link()
    {
        var id = await ProductAsync();

        // Exactly what the catalogue screen sends: the link is left out on purpose.
        (await UpdateAsync(new UpdateProductCommand(id, "Crédit commerçant v2", "Nouvelle description")))
            .IsSuccess.Should().BeTrue();

        await using var db = _db.CreateContext();
        var product = await db.ProductSpecialities.SingleAsync();
        product.Name.Should().Be("Crédit commerçant v2");
        product.BusinessPlatformName.Should().Be("SAB");
        product.BusinessProductId.Should().Be("P-0042");
    }

    [Fact]
    public async Task Custom_values_are_checked_against_the_product_form_and_returned_by_the_detail()
    {
        var id = await ProductAsync(linked: false);

        await using (var db = _db.CreateContext())
        {
            var defaults = EntityFormResolver.Resolve(FormEntity.Product, 0, null);
            var layout = EntityFormLayout.Create(_tenantId, FormEntity.Product);
            layout.Replace(new FormSectionsAndFields(defaults.Sections,
                [.. defaults.Fields, new FormField("maxTenor", FormFieldType.Integer, "Durée maximale (mois)", 9, "general", Max: 120)]),
                0, Guid.NewGuid(), DateTimeOffset.UtcNow).IsSuccess.Should().BeTrue();
            db.EntityFormLayouts.Add(layout);
            await db.SaveChangesAsync();
        }

        var values = (int months) => new Dictionary<string, JsonElement> { ["maxTenor"] = JsonSerializer.SerializeToElement(months) };

        (await UpdateAsync(new UpdateProductCommand(id, "Crédit commerçant", CustomFields: values(240))))
            .Error.Should().Be("CUSTOM_FIELD_OUT_OF_RANGE:maxTenor");
        (await UpdateAsync(new UpdateProductCommand(id, "Crédit commerçant", CustomFields: values(36))))
            .IsSuccess.Should().BeTrue();

        await using var read = _db.CreateContext();
        var dto = (await new GetProductHandler(read).Handle(new GetProductQuery(id), default)).Value;
        dto.CustomFields!["maxTenor"].GetInt32().Should().Be(36);
    }
}
