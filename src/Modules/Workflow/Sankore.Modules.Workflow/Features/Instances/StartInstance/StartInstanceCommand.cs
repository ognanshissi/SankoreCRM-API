using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Instances.StartInstance;

public sealed record StartInstanceCommand(
    string EntityType,
    Guid EntityId) : IRequest<Result<Guid>>, ICommand;
