namespace Sankore.Modules.Integration.Features.Telephony.UserLinks.GetDirectory;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>
/// The provider's own user directory, paired against CRM accounts by address (TEL-B-05).
///
/// <para>
/// A query, so neither <c>TransactionBehavior</c> nor <c>AuditBehavior</c> wraps it: it reads the
/// provider and writes nothing. The audit row belongs on the CONFIRMATION, which is where a link
/// is actually created.
/// </para>
/// </summary>
internal sealed record GetTelephonyDirectoryQuery : IRequest<Result<TelephonyDirectoryDto>>;
