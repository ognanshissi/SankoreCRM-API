namespace Sankore.Modules.Kyc.Features.Routing.RouteRemoteFile;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Applies the product's policy to a remotely collected file whose verification has come back
/// (KRM-B-08).
///
/// <para>
/// <b>No endpoint maps it, and none may.</b> This is the one command in the module that can grant
/// a KYC tier with no human approver, and the only thing entitled to send it is the verification
/// outcome of a file the customer submitted themselves. A route — however permissioned — would
/// turn "the machine decided" into "somebody asked the machine to decide", which is a different
/// act with a different accountability and is not what section 3.2 authorises.
/// </para>
///
/// <para>
/// It carries the tenant explicitly because its sender is a consumer: the ambient tenant of a
/// MassTransit consumer is whatever the message's own scope established, and every cross-boundary
/// read below re-applies the predicate rather than relying on it.
/// </para>
/// </summary>
/// <param name="VerificationCompletedAt">
/// When the verification this routing answers finished — used only for the log line and for the
/// idempotency reasoning in the handler's remarks. Not persisted: the file already carries
/// <c>UpdatedAt</c>, and the assessment carries its own timestamp.
/// </param>
internal sealed record RouteRemoteKycFileCommand(
    Guid TenantId,
    Guid KycFileId,
    DateTimeOffset VerificationCompletedAt
) : IRequest<Result<RouteRemoteKycFileResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "KycFile";
    public string? ResourceId => KycFileId.ToString();
}

/// <param name="Routed">
/// False when there was nothing to do — the file is not remote, or it already carries a route.
/// Both are ordinary: the consumer sees every verification of every channel, and a broker may
/// redeliver.
/// </param>
/// <param name="Automatic">True when the file was approved with no human.</param>
/// <param name="Reason">
/// The routing reason code when a human was asked, null when it was not. One of
/// <c>KycRoutingReasons</c>.
/// </param>
internal sealed record RouteRemoteKycFileResult(
    Guid KycFileId,
    bool Routed,
    bool Automatic,
    string? Reason,
    string FileStatus,
    string Tier);
