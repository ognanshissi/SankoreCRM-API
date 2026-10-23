namespace Sankore.Modules.Administration.Tests.Features.EntityForms;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Administration.Features.EntityForms.GetEntityForm;
using Sankore.Modules.Administration.Features.EntityForms.SaveEntityForm;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.Infrastructure.Configurations;
using Sankore.Modules.Administration.PublicApi.Forms;
using Sankore.Modules.Administration.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Xunit;

public sealed class EntityFormHandlersTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _db;
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public EntityFormHandlersTests()
    {
        _db = new TestAdminDbContextFactory(_tenantId);
        _user.Id.Returns(Guid.NewGuid());
        _user.TenantId.Returns(_tenantId);
    }

    public void Dispose() => _db.Dispose();

    private async Task<EntityFormDto> GetAsync(FormEntity entity, ITenantModules? modules = null)
    {
        await using var db = _db.CreateContext();
        return (await new GetEntityFormHandler(db, new FixedTenantContext(_tenantId), modules ?? FixedTenantModules.All)
            .Handle(new GetEntityFormQuery(entity), default)).Value;
    }

    private async Task<Sankore.Shared.Kernel.Result<EntityFormDto>> SaveAsync(
        FormEntity entity, int expectedVersion, IReadOnlyList<FormSection> sections, IReadOnlyList<FormField> fields)
    {
        await using var db = _db.CreateContext();
        return await new SaveEntityFormHandler(db, new Sankore.Shared.Kernel.FixedTenantContext(_tenantId), _user, TimeProvider.System)
            .Handle(new SaveEntityFormCommand(entity, expectedVersion, sections, fields), default);
    }

    [Fact]
    public async Task A_saved_custom_field_comes_back_on_the_next_read_and_through_the_module_contract()
    {
        var form = await GetAsync(FormEntity.Client);
        var segment = new FormField("segment", FormFieldType.SingleChoice, "Segment", 1, "contact",
            Options: [new FormOption("PME", "PME"), new FormOption("GE", "Grande entreprise")],
            VisibleWhen: new FormCondition("clientType", ["Legal"]));

        var saved = await SaveAsync(FormEntity.Client, 0, form.Sections, [.. form.Fields, segment]);
        saved.IsSuccess.Should().BeTrue(saved.Error);

        var reread = await GetAsync(FormEntity.Client);
        reread.Version.Should().Be(1);
        reread.Fields.Should().ContainSingle(f => f.Key == "segment")
            .Which.Options!.Select(o => o.Value).Should().Equal("PME", "GE");

        // What Leads and Customers read, from outside a request: the tenant is explicit.
        await using var db = _db.CreateContext();
        var facade = new AdministrationModuleFacade(db, new ProductCatalogCache());
        (await facade.GetEntityFormAsync(_tenantId, FormEntity.Client, default))
            .Fields.Should().Contain(f => f.Key == "segment");
        (await facade.GetEntityFormAsync(Guid.NewGuid(), FormEntity.Client, default))
            .Version.Should().Be(0, "another tenant's layout is not this one's");
    }

    [Fact]
    public async Task Two_editors_saving_from_the_same_version_cannot_overwrite_each_other()
    {
        var form = await GetAsync(FormEntity.Product);

        (await SaveAsync(FormEntity.Product, 0, form.Sections, form.Fields)).IsSuccess.Should().BeTrue();
        (await SaveAsync(FormEntity.Product, 0, form.Sections, form.Fields)).Error.Should().Be("FORM_VERSION_CONFLICT");
    }

    [Fact]
    public void The_stored_document_round_trips_through_its_jsonb_converter()
    {
        var form = Sankore.Modules.Administration.Domain.Forms.EntityFormResolver.Resolve(FormEntity.Lead, 0, null);
        var document = new Sankore.Modules.Administration.Domain.Forms.FormLayoutDocument(form.Sections, form.Fields);

        var json = FormLayoutDocumentConverter.Serialize(document);
        var back = FormLayoutDocumentConverter.Deserialize(json);

        json.Should().Contain("\"SingleChoice\"", "enums are stored by name");
        FormLayoutDocumentConverter.Serialize(back).Should().Be(json);
        new FormLayoutDocumentComparer().Equals(document, back).Should().BeTrue();
    }
}
