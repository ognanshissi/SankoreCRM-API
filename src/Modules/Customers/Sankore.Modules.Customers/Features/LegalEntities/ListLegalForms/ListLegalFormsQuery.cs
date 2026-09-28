namespace Sankore.Modules.Customers.Features.LegalEntities.ListLegalForms;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>
/// The closed, tenant-configurable list of legal forms a company can take.
/// A query: no <c>ICommand</c>, therefore no transaction and no audit entry.
/// </summary>
public sealed record ListLegalFormsQuery(bool IncludeInactive = false)
    : IRequest<Result<IReadOnlyList<LegalFormDto>>>;

public sealed record LegalFormDto(
    Guid Id,
    string Code,
    string Label,
    bool IsActive,
    int DisplayOrder);
