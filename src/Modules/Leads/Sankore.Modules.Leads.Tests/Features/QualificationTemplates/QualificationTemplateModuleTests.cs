namespace Sankore.Modules.Leads.Tests.Features.QualificationTemplates;

using FluentAssertions;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.QualificationTemplates.CreateQualificationTemplate;
using Sankore.Modules.Leads.Features.QualificationTemplates.UpdateQualificationTemplate;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Same rule as product creation: no NEW template in the category of a module the tenant has not
/// switched on, and a template already there keeps being editable.
/// </summary>
public sealed class QualificationTemplateModuleTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly TimeProvider _clock = TimeProvider.System;

    public QualificationTemplateModuleTests() => _factory = new TestDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    private static readonly QuestionInput[] Questions = [new("Revenu mensuel ?", QuestionType.Numeric, 10)];

    private async Task<Result<Guid>> CreateAsync(ProductCategory? category, ITenantModules modules)
    {
        await using var db = _factory.CreateContext();
        return await new CreateQualificationTemplateHandler(db, _clock, modules).Handle(
            new CreateQualificationTemplateCommand(_tenantId, "Entretien", null, category, null, Questions, null),
            CancellationToken.None);
    }

    private async Task<Result> UpdateAsync(Guid id, ProductCategory? category, ITenantModules modules)
    {
        await using var db = _factory.CreateContext();
        return await new UpdateQualificationTemplateHandler(db, modules).Handle(
            new UpdateQualificationTemplateCommand(id, "Entretien modifié", null, category, null, Questions, null),
            CancellationToken.None);
    }

    [Fact]
    public async Task A_template_in_a_banking_category_is_refused_without_the_module()
    {
        (await CreateAsync(ProductCategory.Loan, FixedTenantModules.None)).Error
            .Should().Be(TenantModuleErrors.CategoryDisabled);
    }

    [Fact]
    public async Task A_Standard_template_needs_no_module()
    {
        (await CreateAsync(ProductCategory.Standard, FixedTenantModules.None)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task An_existing_template_cannot_be_moved_into_a_closed_category()
    {
        // Only the refusal is exercised: the accepted path rewrites the questions with
        // ExecuteDeleteAsync, which the InMemory provider does not run. Keeping the SAME category
        // is never refused — the handler compares with the stored one before asking the modules.
        var id = (await CreateAsync(ProductCategory.Standard, FixedTenantModules.All)).Value;

        (await UpdateAsync(id, ProductCategory.Savings, FixedTenantModules.None)).Error
            .Should().Be(TenantModuleErrors.CategoryDisabled);
    }
}
