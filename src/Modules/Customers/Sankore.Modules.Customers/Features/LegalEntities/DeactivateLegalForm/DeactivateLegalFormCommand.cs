namespace Sankore.Modules.Customers.Features.LegalEntities.DeactivateLegalForm;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Retires a legal form from the tenant's list. NEVER a physical delete: existing clients
/// keep a valid <c>LegalFormCode</c> and their forms stay readable, only new creations are
/// refused with <c>LEGAL_FORM_UNKNOWN</c>.
/// </summary>
public sealed record DeactivateLegalFormCommand(string Code)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "LegalForm";
    public string? ResourceId => Code;
}
