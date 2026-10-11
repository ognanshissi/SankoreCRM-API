namespace Sankore.Modules.Administration.Features.Credentials.ListCredentials;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>
/// A user's credential history, current row first. The history is the point — a controller asks
/// what was valid on a past date — so superseded rows are returned, never hidden.
/// </summary>
internal sealed record ListCredentialsQuery(Guid UserId)
    : IRequest<Result<IReadOnlyList<CredentialDto>>>;
