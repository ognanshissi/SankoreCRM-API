namespace Sankore.Modules.Customers.Features.LegalEntities.CreateLegalForm;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Adds a legal form to the tenant's closed list. Idempotent: re-posting an existing
/// code returns the existing row with <c>Created = false</c> instead of failing, so a
/// retried request never produces a duplicate and never needs an error code of its own.
/// </summary>
public sealed record CreateLegalFormCommand(
    string Code,
    string Label,
    int DisplayOrder
) : IRequest<Result<CreateLegalFormResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "LegalForm";

    /// <summary>The code is the natural key per tenant — the audit trail keys on it.</summary>
    public string? ResourceId => Code;
}

/// <param name="Created">False when the code already existed (idempotent replay).</param>
public sealed record CreateLegalFormResult(Guid Id, string Code, bool Created, bool IsActive);
