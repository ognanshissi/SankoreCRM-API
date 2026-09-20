namespace Sankore.Modules.Notifications.Features.EmailTemplates.ForkEmailTemplate;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Creates a tenant-scoped, mutable copy of a system template.
/// The admin can then modify the fork without affecting the original.
/// </summary>
internal sealed record ForkEmailTemplateCommand(Guid SourceTemplateId)
    : IRequest<Result<Guid>>, ICommand;
