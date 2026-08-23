namespace Sankore.Modules.Notifications.Features.EmailTemplates.ActivateEmailTemplate;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Makes the specified template version active, deactivating all other versions
/// for the same (TenantId, TemplateKey, Locale) combination.
/// </summary>
internal sealed record ActivateEmailTemplateCommand(Guid TemplateId)
    : IRequest<Result>, ICommand;
