namespace Sankore.Modules.Leads.Tests.Features.ProjectTemplates;

using FluentAssertions;
using MediatR;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.ProjectTemplates;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Modules.Workflow.PublicApi;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>The ready-made « Dossier de crédit » and what goes to the core banking (2026-11-05).</summary>
public sealed class CreditFileTemplateTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestDbContextFactory _db;
    private readonly IWorkflowModule _workflow = Substitute.For<IWorkflowModule>();
    private readonly IAdministrationModule _administration = Substitute.For<IAdministrationModule>();
    private readonly ISender _sender = Substitute.For<ISender>();

    public CreditFileTemplateTests()
    {
        _db = new TestDbContextFactory(_tenantId);
        _workflow.EnsureProjectCircuitAsync(default!, default, default!, default).ReturnsForAnyArgs(Result.Ok("circuit"));
        _sender.Send(Arg.Any<CreateProjectTemplateCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Ok(Guid.NewGuid()));
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void The_template_is_one_the_domain_accepts()
    {
        var created = ProjectTemplate.Create(
            Guid.NewGuid(), CreditFileTemplate.Name, ProductCategory.Loan, null,
            CreditFileTemplate.Steps("analyse-et-decision-de-credit"), DateTimeOffset.UtcNow,
            CreditFileTemplate.Forecast, CreditFileTemplate.Fields);

        created.IsSuccess.Should().BeTrue(created.Error);
        created.Value!.Steps.Should().ContainSingle(s => s.IsApproval).Which.Label.Should().Be("Accord");
        created.Value.Steps.Should().ContainSingle(s => s.WorkflowCircuit != null);
        created.Value.AmountKind.Should().Be(ProjectAmountKind.Loan);
    }

    [Fact]
    public void Declared_income_is_not_a_project_field()
    {
        // A project's fields are stored in clear; the client record encrypts income.
        CreditFileTemplate.Fields.Should().NotContain(f =>
            f.Label.Contains("revenus mensuels", StringComparison.OrdinalIgnoreCase)
            || f.Label.Contains("charges", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("""{"dureeMois":24,"objetCredit":"Fonds de roulement"}""", 24, "Fonds de roulement")]
    [InlineData("""{"dureeMois":"36"}""", 36, null)]
    [InlineData("""{"dureeMois":0,"objetCredit":"  "}""", null, null)]
    [InlineData("""{"dureeMois":"beaucoup"}""", null, null)]
    [InlineData(null, null, null)]
    public void Fields_are_read_as_stored_or_as_absent(string? json, int? term, string? purpose)
    {
        CreditFileTemplate.ReadFields(json).Should().Be((term, purpose));
    }

    [Fact]
    public void Only_a_loan_with_a_duration_is_a_credit_file()
    {
        var fields = new[] { new ProjectFieldDefinition(CreditFileTemplate.TermFieldKey, "Durée", ProjectFieldType.Integer) };

        CreditFileTemplate.IsCreditFile(Template(ProjectAmountKind.Loan, fields)).Should().BeTrue();
        CreditFileTemplate.IsCreditFile(Template(ProjectAmountKind.Loan, [])).Should().BeFalse("an existing loan template stays as it was");
        CreditFileTemplate.IsCreditFile(Template(ProjectAmountKind.Premium, fields)).Should().BeFalse();
    }

    [Fact]
    public async Task A_product_takes_the_model_even_when_its_family_has_a_template()
    {
        await using (var seed = _db.CreateContext())
        {
            seed.ProjectTemplates.Add(ProjectTemplate.Create(
                _tenantId, "Crédit", ProductCategory.Loan, null,
                CreditFileTemplate.Steps("x"), DateTimeOffset.UtcNow, CreditFileTemplate.Forecast, CreditFileTemplate.Fields).Value!);
            await seed.SaveChangesAsync();
        }
        Product("CR1", "Loan");

        var family = await CreateAsync(null);
        family.Error.Should().Be(ProjectTemplateErrors.ScopeTaken);

        (await CreateAsync("cr1")).IsSuccess.Should().BeTrue();
        await _sender.Received(1).Send(
            Arg.Is<CreateProjectTemplateCommand>(c => c.ProductCode == "CR1" && c.Category == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Only_a_credit_product_takes_the_model()
    {
        Product("OBS", "Insurance");

        (await CreateAsync("OBS")).Error.Should().Be(ProjectTemplateErrors.NotCreditProduct);
        (await CreateAsync("INCONNU")).Error.Should().Be(ProjectTemplateErrors.UnknownProduct);
        await _workflow.DidNotReceiveWithAnyArgs().EnsureProjectCircuitAsync(default!, default, default!, default);
    }

    private void Product(string code, string category) =>
        _administration.GetProductAsync(_tenantId, code, Arg.Any<CancellationToken>())
            .Returns(new ProductSummary(code, code, category, true));

    private async Task<Result<Guid>> CreateAsync(string? productCode)
    {
        await using var db = _db.CreateContext();
        return await new CreateCreditFileTemplateHandler(db, _workflow, _administration, new FixedTenantContext(_tenantId), _sender)
            .Handle(new CreateCreditFileTemplateCommand(productCode), default);
    }

    private static ResolvedProjectTemplate Template(ProjectAmountKind kind, IReadOnlyList<ProjectFieldDefinition> fields) =>
        new(Guid.NewGuid(), "T", "family", [], AmountKind: kind, Fields: fields);
}
