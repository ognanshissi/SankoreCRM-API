using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.CreateTemplate;

public sealed record CreateTemplateCommand(
    string EntityType,
    string Name,
    string? Description) : IRequest<Result<Guid>>, ICommand;
