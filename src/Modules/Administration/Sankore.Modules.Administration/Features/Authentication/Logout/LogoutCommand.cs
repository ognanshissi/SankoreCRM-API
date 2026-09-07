using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Authentication.Logout;

public sealed record LogoutCommand(Guid UserId) : IRequest<Result>, ICommand;