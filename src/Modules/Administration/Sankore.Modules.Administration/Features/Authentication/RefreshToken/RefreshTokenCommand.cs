using MediatR;
using Sankore.Modules.Administration.Features.Authentication.Login;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Authentication.RefreshToken;

public sealed record RefreshTokenCommand(string Token) : IRequest<Result<LoginResult>>, ICommand;
