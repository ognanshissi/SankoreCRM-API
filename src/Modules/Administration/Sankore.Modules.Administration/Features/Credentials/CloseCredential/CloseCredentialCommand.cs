namespace Sankore.Modules.Administration.Features.Credentials.CloseCredential;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Withdraws a user's current credential without replacing it — a suspension by the authority, a
/// departure, a card handed back.
///
/// <para>
/// Not a DELETE, and the route says so: the row is closed, never removed. A register whose rows
/// can disappear cannot answer what was valid on a past date, which is the only question it
/// exists for.
/// </para>
/// </summary>
internal sealed record CloseCredentialCommand(Guid UserId, string Reason)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "DistributionCredential";
    public string? ResourceId => UserId.ToString();
}

internal sealed record CloseCredentialRequest(string Reason);
