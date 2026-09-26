namespace Sankore.Modules.Leads.Features.Import.ImportFromGoogleContacts;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Contacts carry no product, language or source, so <see cref="ImportDefaults"/>
/// is required here — without it every imported row would fail validation.
/// </summary>
public sealed record ImportFromGoogleContactsCommand(
    Guid TenantId,
    Guid InitiatedBy,
    ImportDefaults Defaults
) : IRequest<Result<ImportLeadsAccepted>>, ICommand, IResourceCommand
{
    public string ResourceType => "LeadImport";
    public string? ResourceId => null;
}
