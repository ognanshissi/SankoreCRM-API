using MediatR;
using Microsoft.AspNetCore.Http;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Authentication.ForgotPassword;

public sealed record ForgotPasswordCommand(string Email) : IRequest<Result<ForgotPasswordResult>>, IResourceCommand
{
    public string ResourceType  => "User";
    public string? ResourceId  => null;
}

public sealed record ForgotPasswordResult(string Message);