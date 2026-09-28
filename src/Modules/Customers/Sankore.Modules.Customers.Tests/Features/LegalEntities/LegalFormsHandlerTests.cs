namespace Sankore.Modules.Customers.Tests.Features.LegalEntities;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.LegalEntities.CreateLegalForm;
using Sankore.Modules.Customers.Features.LegalEntities.DeactivateLegalForm;
using Sankore.Modules.Customers.Features.LegalEntities.ListLegalForms;
using Sankore.Modules.Customers.Tests.TestSupport;
using Xunit;

/// <summary>The tenant-configurable closed list backing LEGAL_FORM_UNKNOWN.</summary>
public sealed class LegalFormsHandlerTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherTenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid UserId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    private readonly TestCustomersDbContextFactory _factory = new(TenantId);

    public void Dispose() => _factory.Dispose();

    private ListLegalFormsHandler ListHandler() => new(_factory.CreateContext());

    private CreateLegalFormHandler CreateHandler()
        => new(_factory.CreateContext(), TestDoubles.CurrentUser(TenantId, UserId));

    private DeactivateLegalFormHandler DeactivateHandler() => new(_factory.CreateContext());

    [Fact]
    public async Task Lists_only_the_active_forms_in_display_order()
    {
        await using (var seed = _factory.CreateContext())
        {
            seed.LegalForms.Add(LegalForm.Create(TenantId, "SA", "Société anonyme", 2));
            seed.LegalForms.Add(LegalForm.Create(TenantId, "SARL", "Société à responsabilité limitée", 1));

            var retired = LegalForm.Create(TenantId, "GIE", "Groupement d'intérêt économique", 3);
            retired.Deactivate();
            seed.LegalForms.Add(retired);

            await seed.SaveChangesAsync();
        }

        var active = await ListHandler().Handle(new ListLegalFormsQuery(), CancellationToken.None);

        active.Value.Select(f => f.Code).Should().Equal("SARL", "SA");

        var all = await ListHandler().Handle(
            new ListLegalFormsQuery(IncludeInactive: true), CancellationToken.None);

        all.Value.Select(f => f.Code).Should().Equal("SARL", "SA", "GIE");
        all.Value.Should().ContainSingle(f => !f.IsActive).Which.Code.Should().Be("GIE");
    }

    [Fact]
    public async Task Does_not_list_the_forms_of_another_tenant()
    {
        await using (var seed = _factory.CreateContext())
        {
            seed.LegalForms.Add(LegalForm.Create(OtherTenantId, "LLC", "Limited liability company", 1));
            await seed.SaveChangesAsync();
        }

        var result = await ListHandler().Handle(
            new ListLegalFormsQuery(IncludeInactive: true), CancellationToken.None);

        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Creates_a_form_and_replays_idempotently()
    {
        var created = await CreateHandler().Handle(
            new CreateLegalFormCommand("SCOOPS", "Société coopérative simplifiée", 7),
            CancellationToken.None);

        created.IsSuccess.Should().BeTrue();
        created.Value.Created.Should().BeTrue();
        created.Value.IsActive.Should().BeTrue();

        // A retried POST must not create a second row: the code is the natural key.
        var replay = await CreateHandler().Handle(
            new CreateLegalFormCommand("SCOOPS", "Société coopérative simplifiée", 7),
            CancellationToken.None);

        replay.Value.Created.Should().BeFalse();
        replay.Value.Id.Should().Be(created.Value.Id);

        await using var assertions = _factory.CreateContext();
        (await assertions.LegalForms.CountAsync(f => f.Code == "SCOOPS")).Should().Be(1);
    }

    [Fact]
    public async Task Deactivates_a_form_without_deleting_it()
    {
        await CreateHandler().Handle(
            new CreateLegalFormCommand("SNC", "Société en nom collectif", 4), CancellationToken.None);

        var result = await DeactivateHandler().Handle(
            new DeactivateLegalFormCommand("SNC"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await using var assertions = _factory.CreateContext();
        var stored = await assertions.LegalForms.SingleAsync(f => f.Code == "SNC");
        stored.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Refuses_to_deactivate_an_unknown_or_already_retired_form()
    {
        var unknown = await DeactivateHandler().Handle(
            new DeactivateLegalFormCommand("NOPE"), CancellationToken.None);
        unknown.Error.Should().Be(CustomerErrors.LegalFormUnknown);

        await CreateHandler().Handle(
            new CreateLegalFormCommand("SCI", "Société civile immobilière", 8), CancellationToken.None);
        await DeactivateHandler().Handle(new DeactivateLegalFormCommand("SCI"), CancellationToken.None);

        var again = await DeactivateHandler().Handle(
            new DeactivateLegalFormCommand("SCI"), CancellationToken.None);
        again.Error.Should().Be(CustomerErrors.LegalFormUnknown);
    }

    [Fact]
    public async Task Cannot_deactivate_the_form_of_another_tenant()
    {
        await using (var seed = _factory.CreateContext())
        {
            seed.LegalForms.Add(LegalForm.Create(OtherTenantId, "LLC", "Limited liability company", 1));
            await seed.SaveChangesAsync();
        }

        var result = await DeactivateHandler().Handle(
            new DeactivateLegalFormCommand("LLC"), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.LegalFormUnknown);

        await using var assertions = _factory.CreateContext();
        var foreign = await assertions.LegalForms
            .IgnoreQueryFilters()
            .SingleAsync(f => f.TenantId == OtherTenantId && f.Code == "LLC");
        foreign.IsActive.Should().BeTrue();
    }
}
